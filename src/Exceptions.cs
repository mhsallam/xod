using System;

namespace Xod
{
    public class RequiredPropertyException : Exception
    {
        public RequiredPropertyException() : base("Null value detected in a required property.") { }
    }
    public class DatabaseFileException : Exception
    {
        public DatabaseFileException() : base("An error occurred while loading database file. Make sure of the database path or set InitialCreate option.") { }
    }
    public class MissingParentKeyException : Exception
    {
        public MissingParentKeyException() : base("Parent property is missing in child reference type.") { }
    }
    public class MissingPrimaryKeyValueException : Exception
    {
        public MissingPrimaryKeyValueException() : base("The object you are trying to persist is lacking a primary key value.") { }
    }
    public class PropertyKeyNameException : Exception
    {
        public PropertyKeyNameException()
            : base("Wrong propery name in ForeignKey or ParentKey attribute.")
        {
        }
    }
    public class PrimaryKeyDataTypeException : Exception
    {
        public PrimaryKeyDataTypeException() : base("Wrong primary key datatype. Primary key can only be a primitive datatype, string or enum.") { }
    }
    public class IndexDataTypeException : Exception
    {
        public IndexDataTypeException() : base("Wrong index datatype. Indices can only be a int, long, string or guid datatype.") { }
    }
    public class AutonumberDataTypeException : Exception
    {
        public AutonumberDataTypeException() : base("Wrong auto number datatype. This feature can only be applyed on numeric datatype.") { }
    }
    //public class ReservedPrimaryKeyException : Exception
    //{
    //    public ReservedPrimaryKeyException() : base("The primary key value is reserved by another object.") { }
    //}
    public class ReservedUniqueKeyException : Exception
    {
        public ReservedUniqueKeyException() : base("One or more unique key value is reserved by another object.") { }
    }
    public class ReservedChildException : Exception
    {
        public ReservedChildException() : base("This child object is reserved by another parent object.") { }
    }
    public class ReservedKeyWordException : Exception
    {
        public ReservedKeyWordException()
            : base("You propably used a reserved keyword as one of the object properties.")
        {
        }
    }
    public class AnynomousTypeException : Exception
    {
        public AnynomousTypeException() : base("Anynomous type properties should be registered by using RegisterType<>() function right after initializing the database.") { }
    }
    public class SecurityException : Exception
    {
        public SecurityException() : base("Unable to open database file, you need a valid password.") { }
        public SecurityException(string message) : base(message) { }
    }
}
