// Liquid-only 3D adaptation of EA's SIGGRAPH 2024 PB-MPM iteration.
// Copyright (c) 2024 Electronic Arts Inc. All rights reserved.
// See PbMpm-LICENSE.txt for the BSD-3-Clause license.

float _PbRelaxation;
float _PbViscosity;
float _PbVolumeBlend;
static const float DisplacementScale = 1048576.0;

// Between prepare/integrate, velocity and affine store displacement and its
// gradient, not velocity. affineX.w carries integrated volume J across steps.
[numthreads(128, 1, 1)]
void PbPrepare(uint id : SV_DispatchThreadID)
{
    if (id >= (uint)_ParticleCount || _Diagnostics[0] != 0) return;
    Particle p = _Particles[id];
    if (!all(isfinite(p.position)) || !all(isfinite(p.velocity)) ||
        !all(isfinite(p.affineX)) || !all(isfinite(p.affineY)) || !all(isfinite(p.affineZ)))
    {
        Fault(1, id);
        return;
    }
    if (any(p.position.xyz < 2) || any(p.position.xyz > _Resolution - 3))
    {
        Fault(4, id);
        return;
    }
    if (p.affineX.w < 0) { Fault(8, id); return; }
    p.velocity.xyz = (p.velocity.xyz + _Acceleration * _Dt) * _Dt;
    p.affineX.xyz *= _Dt;
    p.affineY.xyz *= _Dt;
    p.affineZ.xyz *= _Dt;
    if (p.affineX.w == 0) p.affineX.w = 1;
    _Particles[id] = p;
}

[numthreads(128, 1, 1)]
void PbProject(uint id : SV_DispatchThreadID)
{
    if (id >= (uint)_ParticleCount || _Diagnostics[0] != 0) return;
    Particle p = _Particles[id];
    float3x3 d = float3x3(p.affineX.xyz, p.affineY.xyz, p.affineZ.xyz);
    d -= 0.5 * _PbViscosity * (d + transpose(d));
    // det((I+D)F) ~= (1+tr(D))*J. In 3D tr(alpha I) = 3 alpha.
    float correction = _PbRelaxation * (1.0 / p.affineX.w - 1.0 - d[0][0] - d[1][1] - d[2][2]) / 3.0;
    d[0][0] += correction;
    d[1][1] += correction;
    d[2][2] += correction;
    p.affineX.xyz = d[0];
    p.affineY.xyz = d[1];
    p.affineZ.xyz = d[2];
    _Particles[id] = p;
}

[numthreads(128, 1, 1)]
void PbScatter(uint id : SV_DispatchThreadID)
{
    if (id >= (uint)_ParticleCount || _Diagnostics[0] != 0) return;
    Particle p = _Particles[id];
    int3 origin;
    float3 w[3];
    Weights(p.position.xyz, origin, w);
    for (int z = 0; z < 3; z++)
    for (int y = 0; y < 3; y++)
    for (int x = 0; x < 3; x++)
    {
        int3 cell = origin + int3(x, y, z);
        if (!ValidCell(cell)) { Fault(4, id); continue; }
        float weight = w[x].x * w[y].y * w[z].z;
        float3 displacement = weight * (p.velocity.xyz + AffineVelocity(p, cell - p.position.xyz));
        uint address = CellIndex(cell) * 4;
        Accumulate(address, weight, MassScale, id);
        Accumulate(address + 1, displacement.x, DisplacementScale, id);
        Accumulate(address + 2, displacement.y, DisplacementScale, id);
        Accumulate(address + 3, displacement.z, DisplacementScale, id);
    }
}

[numthreads(128, 1, 1)]
void PbGrid(uint id : SV_DispatchThreadID)
{
    if (id >= (uint)_CellCount || _Diagnostics[0] != 0) return;
    uint address = id * 4;
    float mass = _Accumulation[address] / MassScale;
    if (mass <= 0) return;
    float3 d = float3(_Accumulation[address + 1], _Accumulation[address + 2],
        _Accumulation[address + 3]) / (DisplacementScale * mass);
    int3 cell = int3(id % _Resolution, (id / _Resolution) % _Resolution, id / (_Resolution * _Resolution));
    d = SphereBoundary(cell, d / _Dt) * _Dt;
    if (cell.x < 3) d.x = max(d.x, 0);
    if (cell.y < 3) d.y = max(d.y, 0);
    if (cell.z < 3) d.z = max(d.z, 0);
    if (cell.x > _Resolution - 4) d.x = min(d.x, 0);
    if (cell.y > _Resolution - 4) d.y = min(d.y, 0);
    if (cell.z > _Resolution - 4) d.z = min(d.z, 0);
    if (!all(isfinite(d))) { Fault(1, 0xffffffff); return; }
    _Grid[id] = float4(d, mass);
}

[numthreads(128, 1, 1)]
void PbGather(uint id : SV_DispatchThreadID)
{
    if (id >= (uint)_ParticleCount || _Diagnostics[0] != 0) return;
    Particle p = _Particles[id];
    int3 origin;
    float3 w[3];
    Weights(p.position.xyz, origin, w);
    float3 d = 0, dx = 0, dy = 0, dz = 0;
    float density = 0;
    for (int z = 0; z < 3; z++)
    for (int y = 0; y < 3; y++)
    for (int x = 0; x < 3; x++)
    {
        int3 cell = origin + int3(x, y, z);
        if (!ValidCell(cell)) { Fault(4, id); continue; }
        float weight = w[x].x * w[y].y * w[z].z;
        float4 node = _Grid[CellIndex(cell)];
        float3 weighted = weight * node.xyz;
        float3 offset = cell - p.position.xyz;
        density += weight * node.w;
        d += weighted;
        dx += 4 * weighted.x * offset;
        dy += 4 * weighted.y * offset;
        dz += 4 * weighted.z * offset;
    }
    if (!all(isfinite(d)) || !all(isfinite(dx)) || !all(isfinite(dy)) ||
        !all(isfinite(dz)) || !isfinite(density) || density <= 0)
    {
        Fault(1, id);
        return;
    }
    float ratio = density / _RestDensity;
    p.velocity = float4(d, ratio);
    // Blend in Eulerian volume only in compression, following the reference.
    if (ratio > 1) p.affineX.w = lerp(p.affineX.w, 1.0 / ratio, _PbVolumeBlend);
    p.affineX.xyz = dx;
    p.affineY.xyz = dy;
    p.affineZ.xyz = dz;
    _Particles[id] = p;
    InterlockedMax(_Diagnostics[1], (uint)round(min(ratio, 100000.0) * 1024));
}

[numthreads(128, 1, 1)]
void PbIntegrate(uint id : SV_DispatchThreadID)
{
    if (id >= (uint)_ParticleCount || _Diagnostics[0] != 0) return;
    Particle p = _Particles[id];
    float3 displacement = p.velocity.xyz;
    if (!all(isfinite(displacement))) { Fault(1, id); return; }
    if (length(displacement) > 0.75) { Fault(16, id); return; }
    float volume = p.affineX.w * (1 + p.affineX.x + p.affineY.y + p.affineZ.z);
    if (!isfinite(volume) || volume <= 0) { Fault(8, id); return; }
    // The published liquid model floors J at 0.1 to bound inverse-volume impulses.
    p.affineX.w = max(volume, 0.1);
    float3 position = p.position.xyz + displacement;
    float3 velocity = displacement / _Dt;
    float3 offset = position - _Sphere.xyz;
    bool contact = false;
    if (_Sphere.w > 0 && dot(offset, offset) < _Sphere.w * _Sphere.w)
    {
        float3 normal = dot(offset, offset) > 1e-10 ? normalize(offset) : float3(0, 1, 0);
        position = _Sphere.xyz + normal * _Sphere.w;
        velocity = SphereBoundary(_Sphere.xyz + normal * (_Sphere.w * 0.999), velocity);
        contact = true;
    }
    for (int axis = 0; axis < 3; axis++)
    {
        if (position[axis] < 2)
        {
            position[axis] = 2;
            velocity[axis] = max(velocity[axis], 0);
            contact = true;
        }
        if (position[axis] > _Resolution - 3)
        {
            position[axis] = _Resolution - 3;
            velocity[axis] = min(velocity[axis], 0);
            contact = true;
        }
    }
    p.position = float4(position, 1);
    p.velocity.xyz = velocity;
    p.affineX.xyz = contact ? 0 : p.affineX.xyz / _Dt;
    p.affineY.xyz = contact ? 0 : p.affineY.xyz / _Dt;
    p.affineZ.xyz = contact ? 0 : p.affineZ.xyz / _Dt;
    _Particles[id] = p;
    InterlockedMax(_Diagnostics[2], (uint)round(min(length(velocity), 100000.0) * 1024));
}

// Whitewater consumes velocities; the constraint iterations used displacements.
[numthreads(128, 1, 1)]
void PbExportGrid(uint id : SV_DispatchThreadID)
{
    if (id >= (uint)_CellCount || _Diagnostics[0] != 0) return;
    float4 node = _Grid[id];
    node.xyz /= _Dt;
    _Grid[id] = node;
}
