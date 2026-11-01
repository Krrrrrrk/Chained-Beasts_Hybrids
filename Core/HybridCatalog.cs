using System;

namespace ChainedBeasts.Hybrids.Core;

/// <summary>Fixed native skill recipes. Native beast IDs do not follow skill-family order.</summary>
public static class HybridCatalog
{
    // Native BeastType order: Lion, Eagle, Bear, Panther, Stag, Rhino, Monkey, Wolf.
    private static readonly int[] FamilyBases = { 0, 3, 6, 9, 15, 18, 21, 12 };
    private static readonly int[,] Partners =
    {
        { 12, 13, 23 },
        { 21, 10, 20 },
        { 18, 19, 17 },
        { 15, 4, 14 },
        { 9, 22, 8 },
        { 6, 7, 5 },
        { 3, 16, 2 },
        { 0, 1, 11 }
    };

    public static int Skill(int beast, int variant)
    {
        ValidateBeast(beast);
        ValidateVariant(variant);
        return FamilyBases[beast] + variant;
    }

    public static int Partner(int beast, int variant)
    {
        ValidateBeast(beast);
        ValidateVariant(variant);
        return Partners[beast, variant];
    }

    public static int Tier(int variant)
    {
        ValidateVariant(variant);
        return 4 + variant;
    }

    /// <summary>The partner's normal unlock is deliberately not a prerequisite.</summary>
    public static bool IsEligible(int variant, bool globallyUnlocked, bool nativeVariantAvailable)
    {
        ValidateVariant(variant);
        return globallyUnlocked && nativeVariantAvailable;
    }

    private static void ValidateBeast(int beast)
    {
        if (beast < 0 || beast >= FamilyBases.Length)
            throw new ArgumentOutOfRangeException(nameof(beast), "Expected a native beast ID from 0 through 7.");
    }

    internal static void ValidateVariant(int variant)
    {
        if (variant < 0 || variant > 2)
            throw new ArgumentOutOfRangeException(nameof(variant), "Expected a normal variant index from 0 through 2.");
    }
}
