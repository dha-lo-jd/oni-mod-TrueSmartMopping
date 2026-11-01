namespace TrueSmartMopping;

/// <summary>
/// Work-order marker for a smart mop chore. Reacts to the vanilla Cancel tool (GameHashes.Cancel)
/// by cancelling the owning chore.
/// </summary>
public sealed class SmartMopMarker : KMonoBehaviour
{
    public SmartMopChore.StatesInstance owner;

    protected override void OnSpawn()
    {
        base.OnSpawn();
        Subscribe((int)GameHashes.Cancel, OnCancel);
    }

    private void OnCancel(object data)
    {
        owner?.OnMarkerCancelled(gameObject);
    }
}
