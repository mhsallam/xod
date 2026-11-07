using System;

namespace Xod
{
    public class RequiredPropertyException : XodException
    {
        public RequiredPropertyException() : base("Null or default value detected in a required property.") { }
        public RequiredPropertyException(string propertyName) : base($"Null or default value detected in required property '{propertyName}'.") { }
    }

    public class MissingParentKeyException : XodException
    {
        public MissingParentKeyException() : base("Parent property is missing in child reference type.") { }
    }

    public class MissingPrimaryKeyValueException : XodException
    {
        public MissingPrimaryKeyValueException() : base("The object you are trying to persist is lacking a primary key property.") { }
    }

    public class PropertyKeyNameException : XodException
    {
        public PropertyKeyNameException() : base("Wrong property name in ForeignKey or ParentKey attribute.") { }
    }

    public class PrimaryKeyDataTypeException : XodException
    {
        public PrimaryKeyDataTypeException() : base("Wrong primary key datatype. Primary key can only be a primitive datatype, string, guid, or enum.") { }
    }

    public class IndexDataTypeException : XodException
    {
        public IndexDataTypeException() : base("Wrong index datatype. Indices can only be a primitive, numeric, string, datetime, or guid datatype.") { }
    }

    public class AutonumberDataTypeException : XodException
    {
        public AutonumberDataTypeException() : base("Wrong auto number datatype. This feature can only be applied on numeric or Guid datatypes.") { }
    }

    public class ReservedPrimaryKeyException : XodException
    {
        public ReservedPrimaryKeyException() : base("The primary key value is reserved by another object.") { }
    }

    public class ReservedUniqueKeyException : XodException
    {
        public ReservedUniqueKeyException() : base("One or more unique key values are reserved by another object.") { }
    }

    public class ReservedChildException : XodException
    {
        public ReservedChildException() : base("This child object is reserved by another parent object.") { }
    }

    public class ReservedKeyWordException : XodException
    {
        public ReservedKeyWordException() : base("You used a reserved keyword as one of the object properties.") { }
    }

    public class AnonymousTypeException : XodException
    {
        public AnonymousTypeException() : base("Anonymous/dynamic type properties should be registered using RegisterType<T>() right after initializing the database.") { }
    }

    /// <summary>
    /// Preserved for backward compatibility with XOD 1.x spelling.
    /// </summary>
    public class AnynomousTypeException : AnonymousTypeException
    {
    }
}
