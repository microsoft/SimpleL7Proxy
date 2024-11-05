import os
from azure.identity import ClientSecretCredential
from azure.mgmt.resource import ResourceManagementClient

def get_token(tenant_id, client_id, client_secret, audience):
    credential = ClientSecretCredential(
        tenant_id=tenant_id,
        client_id=client_id,
        client_secret=client_secret
    )
    
    token = credential.get_token(audience)
    return token.token

# Read from environment variables
tenant_id = os.getenv('AZURE_TENANT_ID')
client_id = os.getenv('AZURE_CLIENT_ID')
client_secret = os.getenv('AZURE_CLIENT_SECRET')
audience = os.getenv('AZURE_AUDIENCE', 'https://management.azure.com/.default')

if not tenant_id or not client_id or not client_secret:
    print("Please set the following environment variables:")
    print("  AZURE_TENANT_ID: Your Azure tenant ID")
    print("  AZURE_CLIENT_ID: Your Azure client ID")
    print("  AZURE_CLIENT_SECRET: Your Azure client secret")
    print("Optional:")
    print("  AZURE_AUDIENCE: The audience for the token (default: 'https://management.azure.com/.default')")
    exit(1)

try:
    token = get_token(tenant_id, client_id, client_secret, audience)
    print(f"Access Token: {token}")
except Exception as e:
    print(e)