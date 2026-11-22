using System;
using System.Globalization;
using System.Runtime.Serialization;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Client;

[assembly: ProxyTypesAssembly]

namespace Xrmd.TestPlugins.EarlyBound
{
    /// <summary>An early-bound <c>pb_pipelog</c>, as a code generator would write it.</summary>
    [DataContract]
    [EntityLogicalName("pb_pipelog")]
    public class PipeLog : Entity
    {
        public const string EntityLogicalName = "pb_pipelog";

        public PipeLog() : base(EntityLogicalName) { }

        [AttributeLogicalName("pb_name")]
        public string Name
        {
            get { return GetAttributeValue<string>("pb_name"); }
            set { SetAttributeValue("pb_name", value); }
        }
    }

    /// <summary>An early-bound <c>pb_pipe</c> (only its name).</summary>
    [DataContract]
    [EntityLogicalName("pb_pipe")]
    public class Pipe : Entity
    {
        public const string EntityLogicalName = "pb_pipe";

        public Pipe() : base(EntityLogicalName) { }

        [AttributeLogicalName("pb_name")]
        public string Name
        {
            get { return GetAttributeValue<string>("pb_name"); }
            set { SetAttributeValue("pb_name", value); }
        }
    }

    /// <summary>
    /// Reads <c>Target</c> as an early-bound <see cref="Pipe"/> and creates an early-bound <see cref="PipeLog"/> named
    /// <c>early|{Target name}|{Depth}</c> with the caller's organization service.
    /// </summary>
    public class EarlyBoundLogPlugin : IPlugin
    {
        public void Execute(IServiceProvider serviceProvider)
        {
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var factory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
            var service = factory.CreateOrganizationService(context.UserId);
            var target = ((Entity)context.InputParameters["Target"]).ToEntity<Pipe>();
            var log = new PipeLog
            {
                Name = string.Format(CultureInfo.InvariantCulture, "early|{0}|{1}", target.Name, context.Depth),
            };
            service.Create(log);
        }
    }
}
