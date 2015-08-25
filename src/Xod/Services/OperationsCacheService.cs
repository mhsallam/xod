using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AcesDevelopers.Xod.Services
{
    internal class OperationsCacheService
    {
        List<ItemCache> Data { get; set; }
        public OperationsCacheService()
        {
            Data = new List<ItemCache>();
        }
        internal void AddItem(Type type, string code, object item, bool lazyLoaded)
        {
            if (item == null)
                return;

            if (!Data.Where(s => s.Code == code).Any())
            {
                Data.Add(new ItemCache()
                {
                    Code = code,
                    Type = type,
                    Item = item,
                    LazyLoaded = lazyLoaded
                });
            }
        }
        internal object GetItem(string code, bool lazyLoaded = false)
        {
            try
            {
                var data = Data.FirstOrDefault(s => s.Code == code && s.LazyLoaded == lazyLoaded);
                if (null != data)
                    return data.Item;
            }
            catch
            {
                return null;
            }
            return null;
        }
        internal void Clear()
        {
            Data.Clear();
        }
    }
}
