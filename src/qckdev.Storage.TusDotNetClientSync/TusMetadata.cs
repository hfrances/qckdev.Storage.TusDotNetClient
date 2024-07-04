using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace qckdev.Storage.TusDotNetClientSync
{
    public sealed class TusMetadata
    {

        public TusMetadata(string key, string value)
        {
            this.key = key;
            this.value = value;
        }

        public string key { get; }
        public string value { get; }

    }
}
