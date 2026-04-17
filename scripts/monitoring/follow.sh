read -p "Enter the replica name: " REPLICA
az containerapp logs show -n nvm2-tc26 -g $GROUP --replica $REPLICA --container nvm2-tc26 --follow
