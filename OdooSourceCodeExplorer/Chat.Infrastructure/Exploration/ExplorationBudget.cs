namespace Chat.Infrastructure.Exploration;

// One instance for investigation, reviews, continuations and final synthesis. Never reset between rounds.
public sealed class ExplorationBudget(int maxOperations = 40, int maxReadCharacters = 180_000)
{
    public int Operations { get; private set; }
    public int ReadCharacters { get; private set; }
    public int Remaining => maxOperations - Operations;
    public bool ReadLimitReached { get; private set; }
    public bool TryUse(int reserve = 0)
    {
        if (Remaining <= reserve) return false;
        Operations++;
        return true;
    }
    public bool TryRead(int characters)
    {
        if (ReadCharacters + characters > maxReadCharacters)
        {
            ReadLimitReached = true;
            return false;
        }
        ReadCharacters += characters;
        return true;
    }
}
