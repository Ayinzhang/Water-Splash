// Scene-local bottle boundary. Parameters and glass share BottleBoundary.Profile.
int _BottleProfileCount;
float4 _BottleProfile[32];
float3 _BottleCenter;
float _BottleClearance;
float _BottleAxisSign;

float BottleDistance(float3 position, out float3 normal)
{
    float3 p = position - _BottleCenter;
    p.x *= _BottleAxisSign;
    float radius = _BottleProfile[0].y;
    float slope = 0;
    for (int i = 0; i < _BottleProfileCount - 1; i++)
    {
        float2 a = _BottleProfile[i].xy, b = _BottleProfile[i + 1].xy;
        if (p.x <= b.x || i == _BottleProfileCount - 2)
        {
            slope = (b.y - a.y) / (b.x - a.x);
            radius = lerp(a.y, b.y, saturate((p.x - a.x) / (b.x - a.x)));
            break;
        }
    }
    float radial = length(p.yz);
    float norm = sqrt(1 + slope * slope);
    float distance = (radial - radius) / norm;
    normal = float3(-slope, radial > 1e-7 ? p.y / radial : 1,
        radial > 1e-7 ? p.z / radial : 0) / norm;
    float back = _BottleProfile[0].x - p.x;
    float front = p.x - _BottleProfile[_BottleProfileCount - 1].x;
    if (back > distance) { distance = back; normal = float3(-1,0,0); }
    if (front > distance) { distance = front; normal = float3(1,0,0); }
    normal.x *= _BottleAxisSign;
    return distance;
}
float3 BottleGridBoundary(float3 position, float3 velocity)
{
    float3 normal;
    if (BottleDistance(position, normal) > -(_BottleClearance + .65))
        velocity -= max(dot(velocity, normal), 0) * normal;
    return velocity;
}
bool BottleProject(inout float3 position, inout float3 velocity)
{
    bool contact = false;
    [unroll] for (int i = 0; i < 8; i++)
    {
        float3 normal;
        float distance = BottleDistance(position, normal) + _BottleClearance;
        if (distance <= 0) break;
        position -= normal * (distance + 1e-4);
        velocity -= max(dot(velocity, normal), 0) * normal;
        contact = true;
    }
    return contact;
}
