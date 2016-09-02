using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using System.Reflection;

namespace Xod
{
    internal class Database
    {
        public Database()
        {
        }
    }

    internal class Table
    {
        public string Name { get; set; }
        public List<TablePage> Pages { get; set; }
        public List<PageIndex> Indexes { get; set; }
        AutonumberCache Seed { get; set; }

        public Table()
        {
            Pages = new List<TablePage>();
        }
    }

    internal class TablePage
    {
        [PropertyAttribute(Position = ValuePosition.Attribute)]
        public bool Full { get; set; }
        public List<PageRow> Rows { get; set; }

        public TablePage()
        {
            Rows = new List<PageRow>();
        }
    }
    internal class PageIndex
    {
        [PropertyAttribute(Position = ValuePosition.Attribute)]
        public string Begins { get; set; }
        [PropertyAttribute(Position = ValuePosition.Attribute)]
        public string Ends { get; set; }
    }

    internal class PageRow
    {
        public string File { get; set; }
        [PropertyAttribute(Position = ValuePosition.Attribute)]
        public bool Full { get; set; }
    }

    internal class IndexRange
    {
        public Type Type { get; set; }
        public Type PropertyType { get; set; }
        public string PropertyName { get; set; }
        
        //for numeric datatypes
        public dynamic Begins { get; set; }
        public dynamic Ends { get; set; }

        //for guid datatype
        public dynamic Pattern { get; set; }

        public string Page { get; set; }
    }


    internal class XRowTree
    {
        public XFile Table { get; set; }
        public XFile Page { get; set; }
        public List<XElement> Rows { get; set; }
    }

    internal class XRow
    {
        public XFile Table { get; set; }
        public XFile Page { get; set; }
        public XElement Row { get; set; }
    }

    internal class XCreatedItem
    {
        public string Code { get; set; }
        public object Item { get; set; }
    }

    internal class XMasterDetails
    {
        public XFile Master { get; set; }
        public List<XMasterDetails> Details { get; set; }

        public XMasterDetails()
        {
            Details = new List<XMasterDetails>();
        }
    }
    internal class XFile : IDisposable
    {
        //private bool compressed;
        string password = null;
        XDocument document = null;

        internal XDocument Document { get { return document; } }

        public string Path { get; private set; }
        public Type Type { get; private set; }

        public XFile(XDocument document, Type type, string path, string password = null) //, bool compressed = false
        {
            this.document = document;
            this.password = password;
            this.Path = path;
        }

        public XElement Root()
        {
            if (document == null)
                return null;

            return document.Root;
        }
        public IEnumerable<XElement> Pages()
        {
            return Get("Pages", "Page");
        }
        public IEnumerable<XElement> Rows()
        {
            return Get("Rows", "Row");
        }

        public string GetFileCode()
        {
            if (!string.IsNullOrEmpty(Path))
                return System.IO.Path.GetFileNameWithoutExtension(Path);
            else
                return null;
        }

        public void Dispose()
        {
            this.document = null;
        }

        private IEnumerable<XElement> Get(string parentName, string childrenName)
        {
            if (document == null)
                return null;

            if (document.Root != null)
            {
                var parentElement = document.Root.Element(parentName);
                if (parentElement != null)
                    return parentElement.Elements(childrenName);
            }

            return Enumerable.Empty<XElement>();
        }
    }

    //internal class XFile : IDisposable
    //{
    //    //private bool compressed;
    //    string password = null;
    //    XDocument doc = null;
    //    Xod.Services.ExceptionService exceptionService = null;

    //    //private static readonly object locker = new object();
    //    //private static Dictionary<string, object> lockeres = null;

    //    public string Path { get; private set; }
    //    public Type Type { get; private set; }
    //    public event EventHandler Changing;
    //    public event EventHandler Changed;

    //    public XFile(string path, Type type, string password = null) //,bool compressed = false, 
    //    {
    //        //if (lockeres == null)
    //        //{
    //        //    lock (locker)
    //        //    {
    //        //        if (lockeres == null)
    //        //            lockeres = new Dictionary<string, object>();
    //        //    }
    //        //}

    //        if (System.IO.File.Exists(path))
    //        {
    //            this.Path = path;
    //            exceptionService = new Services.ExceptionService(this.Path);
    //            this.password = password;
    //            //this.compressed = compressed;

    //            //no need for lock(), it is been called by lock-enabled method
    //            if (!string.IsNullOrEmpty(password))
    //            {
    //                try
    //                {
    //                    this.doc = XDocument.Parse(FileCryptoHelper.DecryptContent(path, password));
    //                }
    //                catch
    //                {
    //                    exceptionService.Throw(new SecurityException());
    //                }
    //            }
    //            //else if (compressed)
    //            //    Document = XDocument.Parse(File.ReadAllText(fileName));
    //            else
    //                using(var reader = new FileStream(this.Path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
    //                {
    //                    this.doc = XDocument.Load(reader);
    //                }
    //            //using (StreamReader sr = new StreamReader(path, true))
    //            //{
    //            //}
    //        }
    //    }

    //    public XFile(XDocument document, string path, Type type, string password = null) //, bool compressed = false
    //    {
    //        //if (lockeres == null)
    //        //    lockeres = new Dictionary<string, object>();

    //        //this.compressed = compressed;
            

    //        //no need for lock(), it is just passing references
    //        doc = document;
    //        this.password = password;
    //        this.Path = path;
    //        exceptionService = new Services.ExceptionService(this.Path);
    //    }

    //    public XElement Root()
    //    {
    //        if (doc == null)
    //            return null;

    //        return doc.Root;
    //    }
    //    public IEnumerable<XElement> Pages()
    //    {
    //        return Get("Pages", "Page");
    //    }
    //    public IEnumerable<XElement> Rows()
    //    {
    //        return Get("Rows", "Row");
    //    }

    //    public void Save()
    //    {
    //        //if (!FileHelper.IsReady(this.Path))
    //        //    exceptionService.Throw(new IOException());


    //        //object locker = null;
    //        //if (lockeres.ContainsKey(this.Path))
    //        //    locker = lockeres[this.Path];
    //        //else
    //        //{
    //        //    locker = new object();
    //        //    lockeres.Add(this.Path, locker);
    //        //}

    //        //lock (locker)
    //        string lockCode = GenerateLock();
    //        lock(lockCode)
    //        {
    //            if (Changing != null)
    //                Changing(this, EventArgs.Empty);

    //            if (!string.IsNullOrEmpty(this.password))
    //            {
    //                StringBuilder builder = new StringBuilder();
    //                using (TextWriter writer = new StringWriter(builder))
    //                    doc.Save(writer);
    //                FileCryptoHelper.EncryptContent(builder.ToString(), this.Path, this.password);
    //            }
    //            else
    //            {
    //                FileStream file = new FileStream(this.Path, FileMode.Create, FileAccess.Write, FileShare.Read);
    //                try
    //                {
    //                    doc.Save(file);
    //                }
    //                finally
    //                {
    //                    file.Close();
    //                }
    //            }

    //            //lockeres.Remove(this.Path);

    //            if (Changed != null)
    //                Changed(this, EventArgs.Empty);
    //        }

    //    }

    //    //public void Save()
    //    //{
    //    //    lock (locker)
    //    //    {
    //    //        if (!string.IsNullOrEmpty(this.password))
    //    //        {
    //    //            StringBuilder builder = new StringBuilder();
    //    //            using (TextWriter writer = new StringWriter(builder))
    //    //                doc.Save(writer);
    //    //            FileCryptoHelper.EncryptContent(builder.ToString(), this.Path, this.password);
    //    //        }
    //    //        //else if (compressed)
    //    //        //    File.WriteAllText(Path, Document.ToString().Compress());
    //    //        else
    //    //            doc.Save(Path);

    //    //        if (null != Changed)
    //    //            Changed(this, EventArgs.Empty);
    //    //    }
    //    //}

    //    public void Save(string path)
    //    {
    //        Path = path;
    //        Save();
    //    }
    //    public long Size()
    //    {
    //        System.IO.FileInfo fi = new FileInfo(Path);
    //        if (fi.Exists)
    //            return fi.Length;
    //        return 0;
    //    }
    //    public string GetFileCode()
    //    {
    //        if (!string.IsNullOrEmpty(Path))
    //            return System.IO.Path.GetFileNameWithoutExtension(Path);
    //        else
    //            return null;
    //    }

    //    public static XFile Load(string fileName, Type type, string password = null) //bool compressed = false, 
    //    {
    //        return new XFile(fileName, type, password);
    //    }

    //    public void Delete()
    //    {
    //        System.IO.FileInfo fi = new FileInfo(Path);
    //        if (fi.Exists)
    //            fi.Delete();
    //    }

    //    private string GenerateLock()
    //    {
    //        string root = System.IO.Path.GetDirectoryName(this.Path);
    //        return string.Format("io://[{0}]", root);
    //    }

    //    public void Dispose()
    //    {
    //        this.doc = null;
    //    }

    //    private IEnumerable<XElement> Get(string parentName, string childrenName)
    //    {
    //        if (doc == null)
    //            return null;

    //        if (doc.Root != null)
    //        {
    //            var parentElement = doc.Root.Element(parentName);
    //            if (parentElement != null)
    //                return parentElement.Elements(childrenName);
    //        }

    //        return Enumerable.Empty<XElement>();
    //    }
    //}

    internal interface ICachedList<T> : IDisposable
    {
        List<T> GetItems();
    }

    internal class AttributeInfoItem
    {
        public Type Type { get; set; }
        public string PropertyName { get; set; }
        public PropertyInfo Property { get; set; }
        public dynamic Attribute { get; set; }
        public PropertyTypeCategory TypeCategory { get; set; }
        public System.Type AttributeType { get; set; }
    }

    internal class ItemCache
    {
        public string Code { get; set; }
        public Type Type { get; set; }
        public object Item { get; set; }
        public bool LazyLoaded { get; set; }
        public string[] IncludedReferenceProperties { get; set; }
        public DateTime LoadTime { get; set; }

        public Guid ReadId { get; set; }
        public Guid ParentReadId { get; set; }
        public bool ParentIsLeaf { get; set; }
    }

    internal enum ItemCacheLoadType
    {
        Direct, Indirect, Unspecified
    }


    internal class FileCache
    {
        public string FileName { get; set; }
        public XFile File { get; set; }
        public DateTime LastCheckout { get; set; }
        public FileCacheStatus Status { get; set; }
    }

    internal enum FileCacheStatus
    {
        Shared, Locked
    }

    internal class PropertyInfoItem
    {
        public Type Type { get; set; }

        public string PropertyName { get; set; }
        public Type PropertyType { get; set; }
        public PropertyInfo Property { get; set; }
        // public PropertyDescriptor Property { get; set; }


        public PropertyTypeCategory TypeCategory { get; set; }
        public PropertyReferenceType ReferenceType { get; set; }
        public CascadeOptions Cascade { get; set; }
        public string ChildParentProperty { get; set; }
        public Type CollectionItemType { get; set; }

        public bool IsGenericType { get; set; }
        public string GenericTypeProperty { get; set; }

        public List<ForeignKeyAttribute> ForeignKeys { get; set; }
        public List<ParentKeyAttribute> ParentKeys { get; set; }

        public string RelationshipName { get; set; }

        public bool IsPrimaryKey { get; set; }
        public bool IsRequired { get; set; }
        public bool IsUnique { get; set; }
        public bool IsNotMapped { get; set; }
        public bool IsMarkup { get; set; }
        public bool IsReadOnly { get; set; }
        public bool IsInherited { get; set; }
        public bool IsIndexed { get; set; }

        public bool IsAutonumber { get; set; }
        //public bool ForceAutoNumber { get; set; }
        public dynamic IdentityIncrement { get; set; }
        public dynamic IdentitySeed { get; set; }

        public ValuePosition ValuePosition { get; set; }
        public dynamic DefaultValue { get; set; }

        public CryptoMethod Encryption { get; set; }

        public PropertyInfoItem()
        {
            this.IdentitySeed = 1;
            this.IdentityIncrement = 1;
        }
    }

    internal class AutonumberCache
    {
        public Type Type { get; set; }
        public string PropertyName { get; set; }
        public dynamic Value { get; set; }
        public MethodInfo Method { get; set; }
    }

    internal class ReadWriteTrack
    {
        public object Item { get; set; }
        public Type Type { get; set; }
        public string Code { get; set; }
        public ReadWriteTrack Parent { get; set; }

        public PropertyInfoItem RootProperty { get; set; }
        public Guid ReadId { get; set; }
    }

    internal enum PropertyTypeCategory: byte
    {
        None = 0, ValueTypeArray = 1, ValueTypeCollection = 2, Class = 3, Array = 4, GenericCollection = 5
    }

    internal enum PropertyReferenceType
    {
        None, Foreign, SelfForeign, Reference, Complex, Children, Parent
    }

    /// <summary>
    /// Trigger action associated object
    /// </summary>
    public class TriggerEventArgs : EventArgs
    {
        public object Item { get; set; }
        public Type Type { get; set; }
        public DatabaseActions Action { get; set; }
        public bool Cancel { get; set; }
    }

    /// <summary>
    /// Trigger action type
    /// </summary>
    public enum DatabaseActions
    {
        Insert,
        Update,
        Delete,
        Drop
    }

    public class DatabaseOptions
    {
        public bool InitialCreate { get; set; }
        //public bool Compressed { get; set; }
        public bool LazyLoad { get; set; }
        public bool LazyLoadParent { get; set; }
    }

    public class UpdateFilter
    {
        public string[] Properties { get; set; }
        public UpdateFilterBehavior Behavior { get; set; }
    }

    public class LazyLoadFilter
    {
        public string[] Properties { get; set; }
    }

    public enum UpdateFilterBehavior
    {
        Target, Skip
    }

    public enum ReservedPropertyType
    {
        Primary, Unique, None
    }
}
