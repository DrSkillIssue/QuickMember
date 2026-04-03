using System;
using System.Reflection;

namespace QuickMember;


internal static class TypeHelpers
{
    public static PropertyInfo[] GetTypeAndInterfaceProperties(this Type type, BindingFlags flags)
    {
        if (!type.IsInterface) return type.GetProperties(flags);

        PropertyInfo[] ownProps = type.GetProperties(flags);
        Type[] interfaces = type.GetInterfaces();
        if (interfaces.Length == 0) return ownProps;

        // Gather all interface property arrays, tracking total count
        var interfaceProps = new PropertyInfo[interfaces.Length][];
        int total = ownProps.Length;
        for (int i = 0; i < interfaces.Length; i++)
        {
            interfaceProps[i] = interfaces[i].GetProperties(flags);
            total += interfaceProps[i].Length;
        }

        var result = new PropertyInfo[total];
        Array.Copy(ownProps, 0, result, 0, ownProps.Length);
        int offset = ownProps.Length;
        for (int i = 0; i < interfaceProps.Length; i++)
        {
            PropertyInfo[] arr = interfaceProps[i];
            Array.Copy(arr, 0, result, offset, arr.Length);
            offset += arr.Length;
        }
        return result;
    }

}
