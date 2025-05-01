using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SimpleL7Proxy;

namespace SimpleL7Proxy;

public static class Banner
{
  public const string VERSION = Constants.VERSION;

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
