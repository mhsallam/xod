using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Xod.Helpers;

namespace Xod.Services
{
    internal class IOService : ICachedList<FileCache>
    {
        const int CACHE_SIZE = 128;
        const int GC_COUNTDOWN_START = 56;
        const int CACHE_TIMEOUT = 5; //in minutes

        private static readonly object locker = new object();
        private static Dictionary<string, object> lockers = null;

        private static Dictionary<string, List<FileCache>> items = null;
        private static Dictionary<string, int> gcCountDown = null;
        private static int instances = 0;
        Func<Type, XDocument> itemWriterDelegate = null;

        internal Func<Type, XDocument> ItemWriterDelegate
        {
            set { itemWriterDelegate = value; }
        }

        string path = null;
        string root = null;
        string password = null;
        PropertyService propertyService = null;

        public IOService(string path, PropertyService propertyService)
            : this(path, null, propertyService)
        {
        }
        public IOService(string path, string password, PropertyService propertyService)
        {
            this.path = path.ToLower();
            this.root = Path.GetDirectoryName(this.path);
            this.password = password;
            this.propertyService = propertyService;

            //double-checking lock pattern; optimized thread-safe
            if (items == null)
            {
                lock (locker)
                {
                    if (items == null)
                    {
                        items = new Dictionary<string, List<FileCache>>();
                        lockers = new Dictionary<string, object>();
                        gcCountDown = new Dictionary<string, int>();
                    }
                }
            }

            //double-checking lock pattern; optimized thread-safe
            if (!items.ContainsKey(this.path))
            {
                lock (locker)
                {
                    if (!items.ContainsKey(this.path))
                    {
                        items.Add(this.path, new List<FileCache>());
                        lockers.Add(this.path, new object());
                        gcCountDown.Add(this.path, GC_COUNTDOWN_START);
                    }
                }
            }
            instances++;
        }

        public List<FileCache> GetItems()
        {
            //no need for lock(): this is index-based item fitching, and the
            //item existance is garanteed through the life of this service instance
            return items[this.path];
        }

        private object GetLock()
        {
            //no need for lock(): this is index-based item fitching, and the
            //item existance is garanteed through the life of this service instance
            return lockers[this.path];
        }

        private bool Contains(string fileName)
        {
            lock (GetLock())
            {
                return this.GetItems().Any(s => s.FileName.ToLower().Equals(fileName.ToLower()));
            }
        }

        //fileName is not the the file full name
        internal bool FileExists(string fileName)
        {
            if (!fileName.ToLower().StartsWith(this.root.ToLower()))
                return File.Exists(string.Format("{0}\\{1}", this.root, fileName));

            return false;
        }

        internal XFile OpenFileOrCreate<T>(string fileName, bool openOnly = false, bool preSave = false)
        {
            return OpenFileOrCreate(fileName, typeof(T), openOnly, preSave);
        }

        internal XFile OpenFileOrCreate(string fileName, Type type = null, bool openOnly = false, bool preSave = false)
        {
            XFile file = null;

            lock (GetLock())
            {
                var item = this.GetItems().FirstOrDefault(s =>
                    s.FileName.ToLower() == fileName.ToLower());

                if (item != null)
                {
                    item.LastCheckout = DateTime.Now;
                    CollectGarbage();
                    return item.File;
                }

                string filePath = Path.Combine(this.root, fileName);
                if (File.Exists(filePath))
                {
                    if (string.IsNullOrEmpty(this.password))
                        file = this.Load(type, filePath);
                    //file = XFile.Load(filePath, type);
                    else
                        file = this.Load(type, filePath, this.password);
                    //file = XFile.Load(filePath, type, this.password);
                }
                else if (!openOnly)
                {
                    XDocument doc = this.itemWriterDelegate(type);
                    file = new XFile(doc, type, filePath, this.password);
                    if (preSave)
                        this.Save(file);
                    //file.Save();
                }

                if (file != null)
                {
                    if (this.GetItems().Count >= CACHE_SIZE)
                    {
                        //double-checking lock pattern; optimized thread-safe
                        var leastActiveItem = this.GetItems().OrderBy(s => s.LastCheckout).FirstOrDefault();
                        if (leastActiveItem != null)
                        {
                            leastActiveItem = this.GetItems().OrderBy(s => s.LastCheckout).FirstOrDefault();
                            this.GetItems().Remove(leastActiveItem);
                        }
                    }

                    FileCache fileCache = new FileCache()
                    {
                        File = file,
                        FileName = fileName,
                        LastCheckout = DateTime.Now
                    };

                    this.GetItems().Add(fileCache);

                    return file;
                }
            }

            return null;
        }

        //returns Type by .xpag files reference code path
        internal Type GetItemType(string referenceCode)
        {
            string[] codeParts = referenceCode.Split('.');
            if (codeParts.Length == 2)
                return GetPageType(codeParts[0]);

            return null;
            //string[] files = System.IO.Directory.GetFiles(root, "*.xtab");
            //foreach (var file in files)
            //{
            //    XFile tableFile = this.ioService.OpenOrCreate(file);
            //    if (tableFile.Pages()
            //        .Where(s => s.Attribute("file").Value == codeParts[0] + ".xpag").Any())
            //    {
            //        string typeString = System.IO.Path.GetFileNameWithoutExtension(file);
            //        return this.propertyService.RegisteredType(typeString);
            //    }
            //}
            //return null;
        }

        //returns the Type by .xpag file code
        internal Type GetPageType(string xpagCode)
        {
            string[] files = System.IO.Directory.GetFiles(root, "*.xtab");
            foreach (var file in files)
            {
                XFile tableFile = this.OpenFileOrCreate(file);
                if (tableFile != null && tableFile.Pages().Where(s => s.Attribute("file").Value == xpagCode + ".xpag").Any())
                {
                    string typeString = System.IO.Path.GetFileNameWithoutExtension(file);
                    return this.propertyService.RegisteredType(typeString);
                }
            }
            return null;
        }

        //returns Type
        internal string GetItemPage(Type type, string code)
        {
            XFile tableFile = this.OpenFileOrCreate(type.FullName + ".xtab");

            ////.NETCore typename compatibility
            //if (tableFile == null)
            //{
            //    string typeName = type.Namespace + "+" + type.Name;
            //    tableFile = this.OpenFileOrCreate(typeName + ".xtab");
            //}

            foreach (var file in tableFile.Pages().Select(s => s.Attribute("file").Value))
            {
                XFile pageFile = this.OpenFileOrCreate(file);
                if (pageFile.Rows().Where(delegate(XElement s)
                {
                    XAttribute sa = s.Attribute("code");
                    return null != sa && sa.Value == code;
                }).Any())
                {
                    return file.Replace(".xpag", "");
                }
            }
            return null;
        }

        internal void Save(XFile file)
        {
            if (file == null)
                return;

            lock (GetLock())
            {
                ChangeLock(file, FileCacheStatus.Locked);

                if (!string.IsNullOrEmpty(this.password))
                {
                    StringBuilder builder = new StringBuilder();
                    using (TextWriter writer = new StringWriter(builder))
                        file.Document.Save(writer);
                    FileCryptoHelper.EncryptContent(builder.ToString(), file.Path, this.password);
                }
                else
                {
                    using(FileStream fs = new FileStream(file.Path, FileMode.Create, FileAccess.Write, FileShare.Read)) {
                        file.Document.Save(fs);
                    }
                }

                ChangeLock(file, FileCacheStatus.Shared);
            }

        }

        internal XFile Load(Type type, string path, string password = null)
        {
            XFile file = null;

            if (System.IO.File.Exists(path))
            {
                //no need for lock(), it is been called by lock-enabled method
                if (!string.IsNullOrEmpty(password))
                {
                    try
                    {
                        lock (GetLock())
                        {
                            XDocument doc = XDocument.Parse(FileCryptoHelper.DecryptContent(path, password));
                            file = new XFile(doc, type, path, password);
                        }
                    }
                    catch
                    {
                        //exceptionService.Throw(new SecurityException());
                    }
                }
                else
                {
                    lock (GetLock())
                    {
                        using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                        {
                            file = new XFile(XDocument.Load(fs), type, path, password);
                        }
                    }
                }
            }

            return file;
        }

        internal XFile OpenPageOrCreate(XFile table)
        {
            lock (GetLock())
            {
                XElement page = table.Pages().FirstOrDefault(delegate(XElement s)
                {
                    XAttribute fullAtt = s.Attribute("full");
                    return fullAtt == null || fullAtt.Value != "true";
                });

                string pageFileName = "";
                if (page == null)
                {
                    pageFileName = string.Format("{0}.{1}", ValueHelper.PickCode(), "xpag");
                    page = new XElement("Page", new XAttribute("file", pageFileName));
                    page.Add(new XAttribute("full", false));
                    table.Root().Element("Pages").Add(page);
                }
                else
                {
                    pageFileName = page.Attribute("file").Value;
                }

                //this.indexService.Index(type, autonumbers, page);
                return this.OpenFileOrCreate(pageFileName, typeof(TablePage));
            }
        }

        internal void Delete(string path)
        {
            if (File.Exists(path))
            {
                lock (GetLock())
                {
                    if (File.Exists(path))
                        File.Delete(path);
                }
            }
        }

        internal long Size(string path)
        {
            if (File.Exists(path))
            {
                lock (GetLock())
                {
                    if (File.Exists(path))
                    {
                        FileInfo fi = new FileInfo(path);
                        return fi.Length;
                    }
                }
            }

            return 0;
        }

        private void ChangeLock(XFile file, FileCacheStatus status)
        {
            if (file == null)
                return;

            lock (GetLock())
            {
                var item = this.GetItems().FirstOrDefault(s => s.File.Path.ToLower() == file.Path.ToLower());
                if (item == null)
                {
                    item = new FileCache()
                    {
                        File = file,
                        FileName = Path.GetFileName(file.Path)
                    };

                    this.GetItems().Add(item);
                }

                item.LastCheckout = DateTime.Now;
                item.Status = status;
            }
        }

        //no need for lock().. it is been called by lock-enabled function
        private void CollectGarbage()
        {
            lock (GetLock())
            {
                gcCountDown[this.path]--;
                if (gcCountDown[this.path] <= 0)
                {
                    gcCountDown[this.path] = GC_COUNTDOWN_START;
                    var timedoutItems = this.GetItems().Where(s => DateTime.Now.TimeOfDay.Subtract(s.LastCheckout.TimeOfDay) > new TimeSpan(0, CACHE_TIMEOUT, 0));
                    if (timedoutItems.Any())
                    {
                        this.GetItems().RemoveAll(s => DateTime.Now.TimeOfDay.Subtract(s.LastCheckout.TimeOfDay) > new TimeSpan(0, CACHE_TIMEOUT, 0));
                    }
                }
            }
        }

        internal void ClearCurrentCache()
        {
            lock (GetLock())
            {
                items[this.path] = new List<FileCache>();
                gcCountDown[this.path] = GC_COUNTDOWN_START;
            }
        }

        internal void ClearAllCache()
        {
            lock (GetLock())
            {
                lockers = null;
                items = null;
                instances = 0;
            }
        }

        //if there is no instances from this service clear shared resources
        public void Dispose()
        {
            instances--;

            if (instances <= 0)
            {
                lock (locker)
                {
                    lockers = null;
                    items = null;
                    instances = 0;
                }
            }
        }
    }
}
