namespace AutoRefreshCache.Sample
{
    internal class Program
    {
        static void Main(string[] args)
        {
            var timer = new Timer(TimerCallback, null, 1000, 2500);

            while (true)
            {
                Thread.Sleep(1000);
            }
        }

        private static void TimerCallback(object state)
        {
            Console.WriteLine($"{Time()} Getting data...");
            var result = AutoRefreshCache.Instance.GetOrRegisterNew<string>("key1", key =>
            {
                Console.WriteLine($"{Time()} ...loading data...");
                Thread.Sleep(5000);
                Console.WriteLine($"{Time()} ...loaded");
                return "test value created at " + Time();
            }, TimeSpan.FromSeconds(7));
            Console.WriteLine($"{Time()} Got {result}");
        }

        private static string Time()
        {
            return DateTime.Now.ToString("HH:mm:ss.fffffff");
        }
    }
}
