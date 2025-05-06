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

**Example Usage:**

I have a container app that was deployed and I wanted to require authentication for it.  I created 2 service principals: 1 for the container app and the second for the client.  I also created a secret for both.  Since the secret will expire in 30 days, its a good idea to get to know how to reset these.  In any case, after hacing these, I ran this command to set it all up:



``` 
G=<Resource group for the container app>
N=<Name of container app>
T=<Tenant for the service principal for the container app>
C=<The clientID / appID for the container app service principal>
S=<The secret for the container app>
A=<The clientID / AppID for the client app service principal>
```

I did need to add a scope for the service principal before running this command. It can be done in the app registration under expose API in the portal.

```./enableContainerAppAuth.sh -g $G -n $N -t $T -c $C -s $S -a $A ```

In order to validate, I logged in as the client service principal using this script:

```
secret=<secret for the client service principal>
clientID=<ClientID for the client service principal>
tenantID=<Tenant / Directory ID for the client service principal>

az login --service-principal --username $clientID --password $secret --tenant $tenantID --allow-no-subscriptions

```

Once logged in, I can then obtain a token for the client service principal to access the container app.  For this, I need the scope that I created in the above step:

```
export token=$(az account get-access-token  --resource api://61e0f881-aace-4fea-bc4a-9468a72aa6d7 --query accessToken -o tsv) ; echo $token
```

Now I can make a call to the container app:
```> curl -k   https://ca.api.4i.com/health -H "Authorization: Bearer $token"```

Here is the expected output:

```
Backend Hosts:                                SimpleL7Proxy: 2.1.16
 Active Hosts: 1  -  All Hosts Operational
 Name: nvmtr.api.4i.com  Status:  -
Worker Statistics:
 Count: 2001 QLen: 0 States: [ deq-2000 pre-1 prxy-0 -[snd-0 rcv-0]-  wr-0 rpt-0 cln-0 ]
User Priority Queue: Users: 0 Total Requests: 0
Request Queue: 0
Event Hub: Enabled  -  0 Items
```



 
