#ifndef WATER_SPLASH_WHITEWATER
#define WATER_SPLASH_WHITEWATER

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
