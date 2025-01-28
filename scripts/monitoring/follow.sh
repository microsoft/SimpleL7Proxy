read -p "Enter the replica name: " REPLICA
az containerapp logs show -n nvmtraca4 -g $GROUP --replica $REPLICA --container nvmtraca4 --follow