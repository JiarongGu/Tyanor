namespace Tyanor.Providers.Local;

/// <summary>
/// The two kinds of thing a machine deployment is made of.
///
/// <para>A machine deployment is HETEROGENEOUS — a directory here, a long-running process there — which is
/// the shape a cloud provider whose every unit is a stack never has. Each unit says which it is, per unit,
/// via <see cref="DeploymentRequest.Option(string, string)"/>.</para>
///
/// <para>The dispatch itself belongs to <see cref="UnitKindDriver"/>, in the framework, because this file and
/// the AWS one were the same file twice. There is no orchestration here and there must never be: ordering,
/// reconcile, retry and the pause/fail decision are the engine's, and a provider that starts branching on run
/// state is writing a second engine inside itself.</para>
/// </summary>
internal sealed class LocalUnitDriver : UnitKindDriver
{
    /// <summary>Build the driver for one machine.</summary>
    /// <param name="root">The machine's deployment root.</param>
    public LocalUnitDriver(string root) : base(LocalOptions.Kind)
    {
        Register(LocalOptions.DirectoryKind, new DirectoryUnit(root));
        Register(LocalOptions.ProcessKind, new ProcessUnit(root));
    }
}
