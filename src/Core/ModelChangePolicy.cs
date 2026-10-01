using System;
using System.Collections.Generic;
using System.Linq;

namespace ClashResolveAI.Core
{
    internal static class ModelChangePolicy
    {
        // Ignore only our known view operations. A product-name prefix alone
        // also suppresses real model edits and mixed transaction groups.
        public static bool IsViewOnly(IEnumerable<string> names)
        {
            var transactions=names.ToList();
            return transactions.Count>0 && transactions.All(n =>
                n.Equals("ClashResolve — Navigate 3D",StringComparison.Ordinal) ||
                n.Equals("ClashResolve — Pin inspection view",StringComparison.Ordinal) ||
                n.Equals("ClashResolve — Navigate 2D",StringComparison.Ordinal) ||
                n.StartsWith("ClashResolve — Create Viewpoint ",StringComparison.Ordinal) ||
                n.Equals("ClashResolveAI — Preview Snap 3D",StringComparison.Ordinal) ||
                n.Equals("ClashResolveAI — Preview Snap 2D",StringComparison.Ordinal));
        }
    }
}
