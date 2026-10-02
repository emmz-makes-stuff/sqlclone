namespace SqlClone;

internal static class Sql
{
    public static string Quote(string name) => "[" + name.Replace("]", "]]") + "]";

    public static string Quote(string schema, string name) => Quote(schema) + "." + Quote(name);

    public static string Literal(string value) => "N'" + value.Replace("'", "''") + "'";
}
