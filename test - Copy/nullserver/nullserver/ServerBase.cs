namespace test.nullserver.nullserver
{
    public class ServerBase
    {
        public ServerBase()
        {
        }

        public int ParseTime(string time)
        {
            if (time.EndsWith("ms"))
            {
                return int.Parse(time.Replace("ms", ""));
            }
            else if (time.EndsWith("s"))
            {
                return int.Parse(time.Replace("s", "")) * 1000;
            }
            else
            {
                throw new FormatException($"Invalid time format: {time}");
            }
        }

    }
}