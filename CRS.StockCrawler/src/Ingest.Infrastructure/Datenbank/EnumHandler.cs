using System.Data;
using Dapper;
using Ingest.Core.Enums;

namespace Ingest.Infrastructure.Datenbank;

/// <summary>
/// Dapper-Typbehandler für die Aufzählungen mit Grundtyp <c>byte</c>.
///
/// <para><b>Warum.</b> Dapper füllt einen Datensatz-Konstruktor nur, wenn der
/// Spaltentyp genau zum Parametertyp passt; bei einer Aufzählung muss der
/// Spaltentyp ihr Grundtyp sein. <c>AssetClass : byte</c> verlangt also eine
/// <c>tinyint</c>-Spalte — die SQL Server liefert, Postgres aber nicht: Dort
/// ist die kleinste Ganzzahl <c>smallint</c>, und Npgsql liefert <c>short</c>.
/// Jeder Datensatz mit einem <c>AssetClass</c>-Parameter scheiterte damit
/// unter Postgres mit „A parameterless default constructor or one matching
/// signature (…, System.Int16 klasse, …) is required".</para>
///
/// <para>Mit einem Typbehandler lässt Dapper die Prüfung des Spaltentyps aus
/// und ruft <see cref="Parse"/>; der nimmt jede Ganzzahl. Der Grundtyp der
/// Aufzählung bleibt <c>byte</c>, weil die SQL-Server-Seite und alle
/// <c>(byte)</c>-Umwandlungen an Parametern davon abhängen. Registriert wird
/// für beide Systeme — unter SQL Server ändert sich nichts, dort kommt
/// weiterhin <c>byte</c> an.</para>
/// </summary>
internal sealed class ByteEnumHandler<T> : SqlMapper.TypeHandler<T> where T : struct, Enum
{
    public static void Registrieren() => SqlMapper.AddTypeHandler(new ByteEnumHandler<T>());

    public override void SetValue(IDbDataParameter parameter, T value)
    {
        parameter.DbType = DbType.Byte;
        parameter.Value = Convert.ToByte(value);
    }

    public override T Parse(object value) => value switch
    {
        T t => t,
        string s => Enum.Parse<T>(s, ignoreCase: true),
        _ => (T)Enum.ToObject(typeof(T), Convert.ToInt32(value)),
    };
}

public static class EnumHandler
{
    private static bool _registriert;

    public static void Registrieren()
    {
        if (_registriert) return;
        _registriert = true;
        ByteEnumHandler<AssetClass>.Registrieren();
        ByteEnumHandler<ProviderId>.Registrieren();
        ByteEnumHandler<CorpActionType>.Registrieren();
    }
}
