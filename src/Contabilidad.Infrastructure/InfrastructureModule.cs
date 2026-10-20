using Autofac;
using Contabilidad.Domain.Canales;
using Contabilidad.Infrastructure.Repositorios;
using Npgsql;

namespace Contabilidad.Infrastructure;

/// <summary>Registro Autofac de la capa de datos y servicios de dominio.</summary>
public sealed class InfrastructureModule(string connectionString, SiiOpciones? sii = null) : Autofac.Module
{
    protected override void Load(ContainerBuilder builder)
    {
        builder.RegisterInstance(sii ?? new SiiOpciones()).AsSelf().SingleInstance();
        TiposDapper.Inicializar();

        builder.Register(_ => new NpgsqlDataSourceBuilder(connectionString).Build())
               .As<NpgsqlDataSource>().SingleInstance();
        builder.RegisterType<ConexionFactory>().As<IConexionFactory>().SingleInstance();

        builder.RegisterType<RepartidorCanales>().AsSelf().SingleInstance();

        builder.RegisterType<CatalogoRepositorio>().As<ICatalogoRepositorio>().InstancePerLifetimeScope();
        builder.RegisterType<CatalogosAdminRepositorio>().As<ICatalogosAdminRepositorio>().InstancePerLifetimeScope();
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
        builder.RegisterType<LiquidacionIvaRepositorio>().As<ILiquidacionIvaRepositorio>().InstancePerLifetimeScope();
        builder.RegisterType<CierreEjercicioRepositorio>().As<ICierreEjercicioRepositorio>().InstancePerLifetimeScope();
        builder.RegisterType<AplicacionResultadoRepositorio>().As<IAplicacionResultadoRepositorio>().InstancePerLifetimeScope();
        builder.RegisterType<InmovilizadoRepositorio>().As<IInmovilizadoRepositorio>().InstancePerLifetimeScope();
        builder.RegisterType<BienesInversionRepositorio>().As<IBienesInversionRepositorio>().InstancePerLifetimeScope();
        builder.RegisterType<CriterioCajaRepositorio>().As<ICriterioCajaRepositorio>().InstancePerLifetimeScope();
        builder.RegisterType<LibrosRegistroRepositorio>().As<ILibrosRegistroRepositorio>().InstancePerLifetimeScope();
        builder.RegisterType<ConciliacionRepositorio>().As<IConciliacionRepositorio>().InstancePerLifetimeScope();
        builder.RegisterType<RemesasRepositorio>().As<IRemesasRepositorio>().InstancePerLifetimeScope();
        builder.RegisterType<BalancesRepositorio>().As<IBalancesRepositorio>().InstancePerLifetimeScope();
        builder.RegisterType<AnaliticaRepositorio>().As<IAnaliticaRepositorio>().InstancePerLifetimeScope();
        builder.RegisterType<PresentacionAeatRepositorio>().As<IPresentacionAeatRepositorio>().InstancePerLifetimeScope();
        builder.RegisterType<SiiRepositorio>().As<ISiiRepositorio>().InstancePerLifetimeScope();
        builder.RegisterType<ModulosRepositorio>().As<IModulosRepositorio>().InstancePerLifetimeScope();
        builder.RegisterType<SociedadesRepositorio>().As<ISociedadesRepositorio>().InstancePerLifetimeScope();
        builder.RegisterType<PresupuestoRepositorio>().As<IPresupuestoRepositorio>().InstancePerLifetimeScope();
    }
}
