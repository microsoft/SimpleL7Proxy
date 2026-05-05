import json
import yaml

with open('deployment.json', 'r') as json_file:
    config = json.load(json_file)

with open('deployment.yaml', 'w') as yaml_file:
    yaml.dump(config, yaml_file, default_flow_style=False)
