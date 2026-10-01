using System;

namespace ClashResolveAI.Core
{
    public static class ClashIdentity
    {
        public static string Pair(string document, long idA, string linkA, long idB, string linkB)
        {
            string a = Uri.EscapeDataString(linkA ?? "") + ":" + idA;
            string b = Uri.EscapeDataString(linkB ?? "") + ":" + idB;
            return Uri.EscapeDataString(document ?? "") + "|" +
                (string.CompareOrdinal(a, b) <= 0 ? a + "|" + b : b + "|" + a);
        }
    }
}
