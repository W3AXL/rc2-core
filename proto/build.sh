#!/bin/bash

# Location of the current script
PROTO_DIR=$(dirname $realpath $0)

# Compile C#
protoc --proto_path=$PROTO_DIR --csharp_out="$PROTO_DIR/../" "$PROTO_DIR"/RC2Proto.proto

# Compile TS
# TODO