using AcesDevelopers.Xod;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using AcesDevelopers.Xod.Playground.Infra;

namespace AcesDevelopers.Xod.Playground
{
    class Program
    {
        static Dictionary<string, object> samples = new Dictionary<string, object>();
        static void Main(string[] args)
        {
            samples.Add("sample1", new Samples.Sample1.Service());
            samples.Add("doc", new Samples.Doc.Service());
            samples.Add("orders", new Samples.Orders.Service());
            samples.Add("rel", new Samples.MultiRel.Service());

            Run();
        }

        static void Run()
        {
            Write(ConsoleColor.White, "::: Please enter the name of the sample you want to execute, type ");
            Write(ConsoleColor.Yellow, "list"); 
            Write(ConsoleColor.White, " to list all available samples or ");
            Write(ConsoleColor.Yellow, "exit");
            WriteLine(ConsoleColor.White, " to close the console");
            Write(ConsoleColor.DarkCyan, "   > ");
            string input = ReadLine(ConsoleColor.Yellow).ToLower();
            if (input == "list")
            {
                ListSamples();
                Write(ConsoleColor.DarkCyan, "   > ");
                input = ReadLine(ConsoleColor.Cyan).ToLower();
            }
            else if (input == "exit")
            {
                Environment.Exit(0);
            }

            if (samples.Keys.Any(s => s == input))
            {
                ISample sampleService = (ISample)samples
                    .FirstOrDefault(s => s.Key == input).Value;

                sampleService.Open();
                sampleService.Init();
                sampleService.Run();

                Again();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                WriteLine(ConsoleColor.Red, "::: Sorry! Sample [{0}] has not been found.", input);
                Console.ForegroundColor = ConsoleColor.White;
                Again();
            }
        }

        private static void ListSamples()
        {
            foreach (var s in samples)
            {
                WriteLine(ConsoleColor.Cyan, "     " + s.Key);
            }
            Console.WriteLine();
        }

        static void Again()
        {
            Console.WriteLine();
            WriteLine(ConsoleColor.White, "::: Do you want to try again? (y = Yes, n = No)");
            Console.ForegroundColor = ConsoleColor.White;

            Write(ConsoleColor.DarkCyan, "   > ");
            string tryAgain = ReadLine(ConsoleColor.Cyan).ToLower();
            if (tryAgain == "y" || tryAgain == "yes")
            {
                Console.WriteLine();
                WriteLine(ConsoleColor.DarkCyan, "--------------------------------------------------------------------------------");
                Console.WriteLine();
                Run();
            }
        }

        static void WriteLine(ConsoleColor color, string text, params object[] arg)
        {
            Console.ForegroundColor = color;
            Console.WriteLine(text, arg);
            Console.ResetColor();
        }
        static void Write(ConsoleColor color, string text, params object[] arg)
        {
            Console.ForegroundColor = color;
            Console.Write(text, arg);
            Console.ResetColor();
        }

        static string ReadLine(ConsoleColor color)
        {
            Console.ForegroundColor = color;
            string input = Console.ReadLine();
            Console.ResetColor();
            return input;
        }
    }
}
