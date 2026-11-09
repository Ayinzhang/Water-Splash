#ifndef MLS_MPM_PARTICLE_STATE_INCLUDED
#define MLS_MPM_PARTICLE_STATE_INCLUDED

struct Particle
{
    float4 position;
    float4 velocity;
    float4 affineX;
    float4 affineY;
    float4 affineZ;
};

#if defined(MPM_PARTICLES_READ_ONLY)
StructuredBuffer<float4> _Particles;
#else
RWStructuredBuffer<float4> _Particles;
#endif
// Zero retains interleaved AoS; N stores five contiguous N-element streams.
uint _ParticlePlaneStride;

uint ParticleFieldIndex(uint id, uint field)
{
    return _ParticlePlaneStride > 0 ? id + field * _ParticlePlaneStride : id * 5 + field;
}

float4 ParticlePosition(uint id) { return _Particles[ParticleFieldIndex(id, 0)]; }
float4 ParticleVelocity(uint id) { return _Particles[ParticleFieldIndex(id, 1)]; }

Particle LoadParticle(uint id)
{
    Particle p;
    p.position = ParticlePosition(id);
    p.velocity = ParticleVelocity(id);
    p.affineX = _Particles[ParticleFieldIndex(id, 2)];
    p.affineY = _Particles[ParticleFieldIndex(id, 3)];
    p.affineZ = _Particles[ParticleFieldIndex(id, 4)];
    return p;
}

#if !defined(MPM_PARTICLES_READ_ONLY)
void StoreParticle(uint id, Particle p)
{
    _Particles[ParticleFieldIndex(id, 0)] = p.position;
    _Particles[ParticleFieldIndex(id, 1)] = p.velocity;
    _Particles[ParticleFieldIndex(id, 2)] = p.affineX;
    _Particles[ParticleFieldIndex(id, 3)] = p.affineY;
    _Particles[ParticleFieldIndex(id, 4)] = p.affineZ;
}
#endif
#endif
