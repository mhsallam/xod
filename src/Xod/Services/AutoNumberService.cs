using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Xod.Services
{
    internal class AutoNumberService
    {
        List<VariableItem> variables { get; set; }

        internal AutoNumberService()
        {
            variables = new List<VariableItem>();
        }
        internal bool Contains(Type type, string propertyName)
        {
            return variables.Any(s => s.Type == type && s.PropertyName == propertyName);
        }
        internal dynamic GetNext(Type type, string propertyName, dynamic increment)
        {
            var variable = variables.FirstOrDefault(s => s.Type == type && s.PropertyName == propertyName);
            if (null != variable)
            {
                variable.Value = variable.Method.Invoke(
                        null,
                        new object[] {
                            new object[]
                            {
                                variable.Value,
                                increment
                            }});
                return variable.Value;
            }

            return Xod.Helpers.ValueHelper.DefaultOf(type.GetProperty(propertyName).PropertyType);
        }
        internal void Set(Type type, string propertyName, MethodInfo method, dynamic value)
        {
            if (!Contains(type, propertyName))
                variables.Add(new VariableItem()
                {
                    PropertyName = propertyName,
                    Value = value,
                    Type = type,
                    Method = method
                });
            else
            {
                var variable = variables.FirstOrDefault(s => s.Type == type && s.PropertyName == propertyName);
                variable.Value = value;
            }
        }
        internal void Update(Type type, string propertyName, dynamic value)
        {
            var variable = variables.FirstOrDefault(s => s.Type == type && s.PropertyName == propertyName);
            if (null != variable)
                variable.Value = value;
        }

        internal void Remove(Type type)
        {
            variables.RemoveAll(s => s.Type == type);
        }
    }
}
