using Autofac;
using Contabilidad.Domain.Canales;
using Contabilidad.Infrastructure.Repositorios;
using Npgsql;

namespace Contabilidad.Infrastructure;

/// <summary>Registro Autofac de la capa de datos y servicios de dominio.</summary>
public sealed class InfrastructureModule(string connectionString) : Autofac.Module
{
    protected override void Load(ContainerBuilder builder)
    {
        TiposDapper.Inicializar();

        builder.Register(_ => new NpgsqlDataSourceBuilder(connectionString).Build())
               .As<NpgsqlDataSource>().SingleInstance();
        builder.RegisterType<ConexionFactory>().As<IConexionFactory>().SingleInstance();

        builder.RegisterType<RepartidorCanales>().AsSelf().SingleInstance();

        builder.RegisterType<CatalogoRepositorio>().As<ICatalogoRepositorio>().InstancePerLifetimeScope();
        builder.RegisterType<CatalogoFacturacionRepositorio>().As<ICatalogoFacturacionRepositorio>().InstancePerLifetimeScope();
        builder.RegisterType<TerceroRepositorio>().As<ITerceroRepositorio>().InstancePerLifetimeScope();
        builder.RegisterType<FacturaRepositorio>().As<IFacturaRepositorio>().InstancePerLifetimeScope();
        builder.RegisterType<AccesoRepositorio>().As<IAccesoRepositorio>().InstancePerLifetimeScope();
        builder.RegisterType<UsuarioRepositorio>().As<IUsuarioRepositorio>().InstancePerLifetimeScope();
        builder.RegisterType<AuditoriaRepositorio>().As<IAuditoriaRepositorio>().InstancePerLifetimeScope();
        builder.RegisterType<EmpresaAdminRepositorio>().As<IEmpresaAdminRepositorio>().InstancePerLifetimeScope();
        builder.RegisterType<CanalRepositorio>().As<ICanalRepositorio>().InstancePerLifetimeScope();
        builder.RegisterType<TesoreriaRepositorio>().As<ITesoreriaRepositorio>().InstancePerLifetimeScope();
        builder.RegisterType<FiscalRepositorio>().As<IFiscalRepositorio>().InstancePerLifetimeScope();
        builder.RegisterType<AsientoRepositorio>().As<IAsientoRepositorio>().InstancePerLifetimeScope();
    }
}
