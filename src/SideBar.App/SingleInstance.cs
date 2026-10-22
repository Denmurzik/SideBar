namespace SideBar;

/// <summary>Один экземпляр на сеанс: именованный мьютекс держится до конца процесса.</summary>
static class SingleInstance
{
    static Mutex? s_mutex;

    public static bool TryAcquire()
    {
        s_mutex = new Mutex(initiallyOwned: true, @"Local\SideBar.SingleInstance", out bool createdNew);
        if (!createdNew) { s_mutex.Dispose(); s_mutex = null; }
        return createdNew;
    }
}
