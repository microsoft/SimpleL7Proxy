The scripts in this directory can be used to enable Oauth2 between 3 applications:
1. Console Application ( which uses a service principal )
2. Container app ( which uses managed identity )
3. APIM ( which only accepts connections from the container app )

The scripts need to be run in the following order:

1. ca2apimSetup.sh
2. console2caSetup.sh
3. enableContainerAppAuth.sh

**Ca2apimSetup**, as the name implies, enables the container app's managed identity to access the APIM. The script accepts the name of an app registration and the managed identity to connect it to.  It creates the app registration and grants access to the managed identity.

**console2caSetup**, enables the console application to call the container app.  Pass in a new app registration name to the call.  The script creates the new app registration and creates the service principal that the console application can use.  It also creates the service principal that should be used to configure the container app.  Once the script completes, it outputs the commands needed to run the next scrpt as well as login details for the console application.

**enableContainerAppAuth.sh**, enables the container app's easyauth modult to validate Oauth2.  The script requires a lot of parameters, thankfully the previous script outputs them to the console.

Usage: $0 -g <resource_group> -n <container_app_name> -t <tenant_id> -c <client_id> -s <client_secret> -a <new_app_id>

 
