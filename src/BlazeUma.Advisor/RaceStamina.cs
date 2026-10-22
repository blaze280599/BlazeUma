using BlazeUma.Data;

namespace BlazeUma.Advisor;

/// <summary>
/// Stamina a trainee needs to run a race's last spurt at full speed, from the race mechanics formulas on
/// uma.guide (Target Speed, HP). No skills, no position keep or randomness, firm ground, Normal mood. The
/// early race is run at the phase's base target speed from the gate (acceleration from 3 m/s is ignored,
/// which slightly overstates early HP use).
/// </summary>
public static class RaceStamina
{
    /// <summary>Style coefficients per phase: early, mid, late (Target Speed page).</summary>
    private static double PhaseCoef(RunningStyle style, int phase) => (style, phase) switch
    {
        (RunningStyle.FrontRunner, 0) => 1.0, (RunningStyle.FrontRunner, 1) => 0.98, (RunningStyle.FrontRunner, _) => 0.962,
        (RunningStyle.PaceChaser, 0) => 0.978, (RunningStyle.PaceChaser, 1) => 0.991, (RunningStyle.PaceChaser, _) => 0.975,
        (RunningStyle.LateSurger, 0) => 0.938, (RunningStyle.LateSurger, 1) => 0.998, (RunningStyle.LateSurger, _) => 0.994,
        (_, 0) => 0.931, (_, 1) => 1.0, _ => 1.0,
    };

    /// <summary>Stamina → HP coefficient per style (HP page).</summary>
    private static double HpCoef(RunningStyle style) => style switch
    {
        RunningStyle.FrontRunner => 0.95, RunningStyle.PaceChaser => 0.89, RunningStyle.LateSurger => 1.0, _ => 0.995,
    };

    /// <summary>Distance aptitude multiplier on late-race speed; grades as stored (8 = S … 1 = G).</summary>
    private static double DistanceProf(int grade) => grade switch
    {
        8 => 1.05, 7 => 1.0, 6 => 0.9, 5 => 0.8, 4 => 0.6, 3 => 0.4, 2 => 0.2, _ => 0.1,
    };

    /// <summary>Stats above 1200 count half in races.</summary>
    public static double Effective(int stat) => stat <= 1200 ? stat : 1200 + (stat - 1200) / 2.0;

    public static double BaseSpeed(int distance) => 20.0 - (distance - 2000) / 1000.0;

    /// <summary>Last spurt target speed (m/s) with full HP.</summary>
    public static double LastSpurtSpeed(int distance, RunningStyle style, int speed, int guts, int distanceGrade)
    {
        double baseSpeed = BaseSpeed(distance);
        double speedTerm = Math.Sqrt(500 * Effective(speed)) * DistanceProf(distanceGrade) * 0.002;
        double lateBaseTarget = baseSpeed * PhaseCoef(style, 2) + speedTerm;
        return (lateBaseTarget + 0.01 * baseSpeed) * 1.05 + speedTerm + Math.Pow(450 * Effective(guts), 0.597) * 0.0001;
    }

    /// <summary>HP per second at a speed: 20 × (v − BaseSpeed + 12)² / 144, times the Guts modifier in the late race.</summary>
    private static double HpPerSecond(double v, double baseSpeed, double gutsMod) =>
        20 * Math.Pow(v - baseSpeed + 12, 2) / 144 * gutsMod;

    /// <summary>
    /// HP the race uses: early (first 1/6) and mid race (to 2/3) at base target speed, then a full last spurt
    /// over the late race. The game's spurt check leaves out the last 60 m, so <paramref name="checkOnly"/>
    /// gives the HP needed to pass that check; otherwise the HP to finish without running dry.
    /// </summary>
    public static double HpNeeded(int distance, RunningStyle style, int speed, int guts, int distanceGrade, bool checkOnly = true)
    {
        double baseSpeed = BaseSpeed(distance);
        double early = distance / 6.0, mid = distance * 2 / 3.0 - early, late = distance / 3.0 - (checkOnly ? 60 : 0);
        double vEarly = baseSpeed * PhaseCoef(style, 0), vMid = baseSpeed * PhaseCoef(style, 1);
        double vSpurt = LastSpurtSpeed(distance, style, speed, guts, distanceGrade);
        double gutsMod = 1 + 200 / Math.Sqrt(600 * Effective(guts));
        return early / vEarly * HpPerSecond(vEarly, baseSpeed, 1)
            + mid / vMid * HpPerSecond(vMid, baseSpeed, 1)
            + late / vSpurt * HpPerSecond(vSpurt, baseSpeed, gutsMod);
    }

    /// <summary>
    /// Stamina stat for a full last spurt: MaxHP = 0.8 × StyleCoef × EffectiveStamina + Distance must cover
    /// <see cref="HpNeeded"/>; above 1200 each effective point costs two stat points. Checked against Umalator
    /// (no skills, Normal mood, firm): this lands at about its 95% spurt rate (Nakayama 2500 pace chaser 1135 vs
    /// 96% at 1100; late surger 1054 vs 94% at 1000 / 98% at 1100; Nakayama 1600 545 vs 87% at 500 / 97% at 600).
    /// <para>
    /// <paramref name="recovery"/> is the HP recovery skills restore in the race, as a share of max HP (skill_data
    /// float_ability_value_1_1 / 10000 for ability type 9: white 150 = 1.5%, gold 550 = 5.5%, as on uma.guide's
    /// skill pages). A skill fires once HP is already below max, so nothing is lost to the cap and the race has
    /// MaxHP × (1 + recovery) to spend: MaxHP ≥ HpNeeded / (1 + recovery). A gold recovery at 2500 m is ~180 HP,
    /// about 250 Stamina for a pace chaser.
    /// </para>
    /// </summary>
    public static int Required(int distance, RunningStyle style, int speed, int guts, int distanceGrade, bool checkOnly = true,
        double recovery = 0)
    {
        double maxHp = HpNeeded(distance, style, speed, guts, distanceGrade, checkOnly) / (1 + Math.Max(0, recovery));
        double effective = Math.Max(0, maxHp - distance) / (0.8 * HpCoef(style));
        return (int)Math.Ceiling(effective <= 1200 ? effective : 1200 + 2 * (effective - 1200));
    }
}
