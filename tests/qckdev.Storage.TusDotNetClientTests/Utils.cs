using System.Linq;
using System.Security.Cryptography;

namespace qckdev.Storage.TusDotNetClientTests
{
    public class Utils
    {
        public static string SHA1(byte[] bytes)
        {
            using (var sha1 = new SHA1Managed())
            {
                return string.Join(
                    "",
                    sha1.ComputeHash(bytes)
                        .Select(b => b.ToString("x2")));
            }
        }
    }
}