namespace Xod
{
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
        Target,
        Skip
    }

    public enum ReservedPropertyType
    {
        Primary,
        Unique,
        None
    }
}
