using System.Data;
using System.Data.Common;
using Godswar.Server.Application.Inventory;

namespace Godswar.Server.Infrastructure.Inventory;

internal static class PostgresHolySpiritBalanceBinding
{
    public const string PhysicalParameterName =
        "cooledPhysicalReductionGradeOneMaximum";
    public const string MagicParameterName =
        "cooledMagicReductionGradeOneMaximum";
    public const string CriticalParameterName =
        "cooledCriticalReductionGradeOneMaximum";

    public static void AddParameters(
        DbCommand command,
        HolySpiritBalanceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(snapshot);
        snapshot.Validate();
        Add(command, PhysicalParameterName,
            snapshot.CooledPhysicalReductionGradeOneMaximum);
        Add(command, MagicParameterName,
            snapshot.CooledMagicReductionGradeOneMaximum);
        Add(command, CriticalParameterName,
            snapshot.CooledCriticalReductionGradeOneMaximum);
    }

    private static void Add(
        DbCommand command,
        string name,
        int value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = DbType.Int32;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
