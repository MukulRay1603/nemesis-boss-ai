using UnityEngine;

/// <summary>
/// A running profile of how the boss's opponent fights, built only from things the boss can see.
/// Each value is an exponential moving average with a ~10 s time constant, reset every fight:
///   Aggression  hits the opponent lands on the boss per second (scaled so 0.5/s reads as 1)
///   Evasion     dodges or dashes started per second (scaled so 0.5/s reads as 1)
///   Range       average distance to the boss / 20
///   Pressure    fraction of time the opponent moves toward the boss
/// Used as 4 extra policy inputs by models with 18 inputs (v5); 14-input models ignore it.
/// Pure arithmetic in FixedUpdate, no randomness, so it never changes seeded results.
/// </summary>
public class PlayerProfile
{
    const float Tau = 10f;   // seconds

    public float Aggression { get; private set; }
    public float Evasion { get; private set; }
    public float Range { get; private set; }
    public float Pressure { get; private set; }

    int hitsThisStep, dodgesThisStep;
    int lastPlayerState = -1;
    Vector3 lastPlayerPos;
    bool hasPos;

    public PlayerProfile() => Reset();

    public void Reset()
    {
        Aggression = 0.3f;
        Evasion = 0.2f;
        Range = 0.25f;
        Pressure = 0.5f;
        hitsThisStep = dodgesThisStep = 0;
        lastPlayerState = -1;
        hasPos = false;
    }

    public void OnBossHit() => hitsThisStep++;

    public void Step(Vector3 bossPos, Vector3 playerPos, int playerState, float dt)
    {
        if (playerState == 4 && lastPlayerState != 4) dodgesThisStep++;   // 4 = Dodge (bot dodge or human dash)
        lastPlayerState = playerState;

        float a = 1f - Mathf.Exp(-dt / Tau);
        float hitRate = hitsThisStep / dt, dodgeRate = dodgesThisStep / dt;
        Aggression = Mathf.Lerp(Aggression, Mathf.Clamp01(hitRate / 0.5f), a);
        Evasion = Mathf.Lerp(Evasion, Mathf.Clamp01(dodgeRate / 0.5f), a);
        Range = Mathf.Lerp(Range, Mathf.Clamp01(Vector3.Distance(bossPos, playerPos) / 20f), a);

        float toward = 0.5f;
        if (hasPos)
        {
            Vector3 v = playerPos - lastPlayerPos;
            Vector3 d = bossPos - playerPos;
            v.y = d.y = 0f;
            if (v.sqrMagnitude > 1e-6f) toward = Vector3.Dot(v.normalized, d.normalized) > 0.2f ? 1f : 0f;
        }
        Pressure = Mathf.Lerp(Pressure, toward, a);
        lastPlayerPos = playerPos;
        hasPos = true;
        hitsThisStep = dodgesThisStep = 0;
    }

    public void Write(float[] obs, int offset)
    {
        obs[offset] = Aggression;
        obs[offset + 1] = Evasion;
        obs[offset + 2] = Range;
        obs[offset + 3] = Pressure;
    }
}
