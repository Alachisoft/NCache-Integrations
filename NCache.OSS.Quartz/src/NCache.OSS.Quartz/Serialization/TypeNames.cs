namespace NCache.OSS.Quartz.Serialization;

internal static class TypeNames
{
    public static string Of(Type type) => $"{type.FullName}, {type.Assembly.GetName().Name}";

    public static Type? Resolve(string name)
    {
        var type = Type.GetType(name, throwOnError: false);

        if (type is not null)
        {
            return type;
        }

        var comma = name.IndexOf(',');
        var fullName = comma < 0 ? name : name[..comma].Trim();

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            type = assembly.GetType(fullName, throwOnError: false);

            if (type is not null)
            {
                return type;
            }
        }

        return null;
    }
}
