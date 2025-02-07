using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SimpleL7Proxy;

public static class Banner
{
  public const string VERSION = "2.0.0d";

  public static void Display()
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
    Console.WriteLine($"Version: {VERSION}");

  }
}
