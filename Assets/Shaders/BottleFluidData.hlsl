#ifndef BOTTLE_FLUID_DATA_INCLUDED
#define BOTTLE_FLUID_DATA_INCLUDED

struct Particle
{
    float4 position;  // Grid position, 1.
    float4 velocity;  // Grid velocity, density ratio.
    float4 affineX;
    float4 affineY;
    float4 affineZ;
};

#if defined(MPM_PARTICLES_READ_ONLY)
StructuredBuffer<float4> _Particles;
#else
RWStructuredBuffer<float4> _Particles;
#endif
uint _ParticlePlaneStride; // Five contiguous streams of this length.

float4 ParticlePosition(uint id) { return _Particles[id]; }
float4 ParticleVelocity(uint id) { return _Particles[id + _ParticlePlaneStride]; }

Particle LoadParticle(uint id)
{
    Particle p;
    p.position = _Particles[id];
    p.velocity = _Particles[id + _ParticlePlaneStride];
    p.affineX = _Particles[id + 2 * _ParticlePlaneStride];
    p.affineY = _Particles[id + 3 * _ParticlePlaneStride];
    p.affineZ = _Particles[id + 4 * _ParticlePlaneStride];
    return p;
}

#if !defined(MPM_PARTICLES_READ_ONLY)
void StoreParticle(uint id, Particle p)
{
    _Particles[id] = p.position;
    _Particles[id + _ParticlePlaneStride] = p.velocity;
    _Particles[id + 2 * _ParticlePlaneStride] = p.affineX;
    _Particles[id + 3 * _ParticlePlaneStride] = p.affineY;
    _Particles[id + 4 * _ParticlePlaneStride] = p.affineZ;
}
#endif

struct Whitewater
{
    float4 position;   // Grid position, remaining lifetime.
    float4 velocity;   // Grid velocity, initial lifetime.
    float4 normalType; // Outward surface normal, 0 = spray / 1 = foam.
};

uint WhitewaterHash(uint value)
{
    value ^= value >> 16;
    value *= 0x7feb352du;
    value ^= value >> 15;
    value *= 0x846ca68bu;
    return value ^ (value >> 16);
}

float WhitewaterRandom(uint seed)
{
    return (WhitewaterHash(seed) & 0x00ffffffu) / 16777216.0;
}

#endif
