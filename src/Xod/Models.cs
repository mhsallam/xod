using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using System.Reflection;
using System.IO;
using AcesDevelopers.Xod.Helpers;

namespace AcesDevelopers.Xod
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

    internal class PageRow
    {
        public string File { get; set; }
        [PropertyAttribute(Position = ValuePosition.Attribute)]
        public bool Full { get; set; }
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

    [DefaultMember("Document")]
    internal class XFile
    {
        //private bool compressed;
        private string password;

        public XDocument Document { get; set; }
        internal FileStream Locker { get; set; }
        public string Path { get; set; }
        public Type Type { get; set; }

        public event EventHandler Changed;

        public XFile(string fileName, Type type, string password = null) //bool compressed = false, 
        {
            if (System.IO.File.Exists(fileName))
            {
                Path = fileName;
                //this.compressed = compressed;
                this.password = password;

                try
                {
                    if (!string.IsNullOrEmpty(password))
                        this.Document = XDocument.Parse(FileCryptoHelper.DecryptContent(fileName, password));
                    //else if (compressed)
                    //    Document = XDocument.Parse(File.ReadAllText(fileName));
                    else
                        using (StreamReader sr = new StreamReader(fileName, true))
                            Document = XDocument.Load(sr);
                }
                catch
                {
                    throw new SecurityException();
                }
            }
        }

        public XFile(XDocument document, string fileName, Type type, string password = null) //, bool compressed = false
        {
            //this.compressed = compressed;
            this.password = password;
            Document = document;
            Path = fileName;
        }

        public void Save()
        {
            if (!string.IsNullOrEmpty(this.password))
            {
                StringBuilder builder = new StringBuilder();
                using (TextWriter writer = new StringWriter(builder))
                    Document.Save(writer);
                FileCryptoHelper.EncryptContent(builder.ToString(), this.Path, this.password);
            }
            //else if (compressed)
            //    File.WriteAllText(Path, Document.ToString().Compress());
            else
                Document.Save(Path);

            if (null != Changed)
                Changed(this, EventArgs.Empty);
        }

        public void Save(string fileName)
        {
            Path = fileName;
            Save();
        }
        public long Size()
        {
            System.IO.FileInfo fi = new FileInfo(Path);
            if (fi.Exists)
                return fi.Length;
            return 0;
        }
        public string GetFileCode()
        {
            if (!string.IsNullOrEmpty(Path))
                return System.IO.Path.GetFileNameWithoutExtension(Path);
            else
                return null;
        }

        public static XFile Load(string fileName, Type type, string password = null) //bool compressed = false, 
        {
            return new XFile(fileName, type, password);
        }

        public void Delete()
        {
            System.IO.FileInfo fi = new FileInfo(Path);
            if (fi.Exists)
                fi.Delete();
        }
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
    }
    internal class PropertyInfoItem
    {
        public Type Type { get; set; }

        public string PropertyName { get; set; }
        public Type PropertyType { get; set; }
        public PropertyInfo Property { get; set; }

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
        public bool IsInherited { get; set; }
        public bool IsIndexed { get; set; }

        public bool IsAutoNumber { get; set; }
        public bool ForceAutoNumber { get; set; }
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

    internal class VariableItem
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
    }

    internal enum PropertyTypeCategory
    {
        None, Class, Array, GenericCollection, ValueTypeCollection, ValueTypeArray
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
