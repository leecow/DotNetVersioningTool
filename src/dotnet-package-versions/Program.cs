using Microsoft.Extensions.CommandLineUtils;
using System;

namespace DotNetVersioningTool
{
    class Program
    {
        static int Main(string[] args)
        {
            var app = new CommandLineApplication();

            var listCommand = app.Command("list", config =>
            {

            });

            var versionArg = new CommandArgument() { Name = "version", Description = "LTS or Current" };

            var updateCommand = app.Command("update", config =>
            {
                config.Arguments.Add(versionArg);
                config.OnExecute(() =>
                {

                    return 0;
                });
            });
            var version = app.Argument("version", "LTS or Current");

            app.OnExecute(() =>
            {

                return 0;
            });

            return app.Execute(args);
        }
    }
}