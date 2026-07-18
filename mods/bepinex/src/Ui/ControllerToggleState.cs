namespace MystiaStewardCompanion.Ui;

/// <summary>
/// 将手柄按下边沿锁存到物理释放，避免一次长按重复切换窗口。
/// </summary>
internal sealed class ControllerToggleState
{
    private bool _latched;

    public bool Update(bool held, bool pressedThisFrame)
    {
        if (!held)
        {
            _latched = false;
            return false;
        }

        if (_latched) return false;
        _latched = true;
        return pressedThisFrame;
    }
}
