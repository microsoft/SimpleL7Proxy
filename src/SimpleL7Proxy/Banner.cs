using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SimpleL7Proxy;

using SimpleL7Proxy.Config;
namespace SimpleL7Proxy;

public static class Banner
{
  public const string VERSION = Constants.VERSION;

  public static void Display(ProxyConfig options)
  {
    Console.WriteLine("=======================================================================================");
    Console.WriteLine(" #####                                 #       ####### ");
    Console.WriteLine("#     #  # #    # #####  #      ###### #       #    #  #####  #####   ####  #    # #   #");
    Console.WriteLine("#        # ##  ## #    # #      #      #           #   #    # #    # #    #  #  #   # #");
    Console.WriteLine(" #####   # # ## # #    # #      #####  #          #    #    # #    # #    #   ##     #");
    Console.WriteLine("      #  # #    # #####  #      #      #         #     #####  #####  #    #   ##     #");
    Console.WriteLine("#     #  # #    # #      #      #      #         #     #      #   #  #    #  #  #    #");
    Console.WriteLine(" #####   # #    # #      ###### ###### #######   #     #      #    #  ####  #    #   #");
    Console.WriteLine("=======================================================================================");
    Console.WriteLine($"Version: {VERSION}  LogLevel: {options.LogLevel}  ContainerApp: {options.ContainerApp}  Replica: {options.ReplicaName}");
  }
}
