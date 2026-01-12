#Getting Started#

The sample function code here will allow you to run the proxy with User Profiles, and a dummy backend.  Once configured, you can perform load testing and scenario validation.

## Prerequisits ##
* Azure function on the flex consumption plan
* Proxy deployed as a container app or running locally

## Deploy ##

Edit the deploy-flex.sh script file at the top of the file. There are two variables:  **RESOURCE_GROUP** and **FUNCTION_APP**.  Assign values for your flex-consumption plan Azure function.

Execute the deploy script: 
```code: sh
./deploy-flex.sh
```


Storage Table Data Contributor
Storage Blob Data Owner


