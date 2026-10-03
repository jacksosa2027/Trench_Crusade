using Godot;

public static class Dice
{
    private static readonly RandomNumberGenerator _rng = new RandomNumberGenerator();

    
    public static int D6()
    {
        return _rng.RandiRange(1, 6);
    }

    public static int D3()
    {
        return _rng.RandiRange(1, 3);
    }

    public static void SetSeed(ulong seed)
    {
        _rng.Seed = seed;
    }

    public static void Randomize()
    {
        _rng.Randomize();
        GD.Print(_rng.Seed);
    }


}