#!/bin/bash

# Filepath to save the URLs
output_file="urls.txt"

# Starting index
start_index=1

# Number of test cases to generate
num_tests=10000

# Base URL
base_url="http://localhost:8000/test-"

# Generate the URLs and write to the file
for ((i=start_index; i<=num_tests; i++))
do
    echo "${base_url}${i}" 
done

#echo "Generated $num_tests test cases in $output_file"