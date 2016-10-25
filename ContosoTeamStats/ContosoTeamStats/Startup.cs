using Microsoft.Owin;
using Owin;

[assembly: OwinStartupAttribute(typeof(ContosoTeamStats.Startup))]
namespace ContosoTeamStats
{
    public partial class Startup
    {
        public void Configuration(IAppBuilder app)
        {
            ConfigureAuth(app);
        }
    }
}
