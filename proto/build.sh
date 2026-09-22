#!/bin/bash

# Location of the current script
PROTO_DIR=$(dirname $realpath $0)

# Location of the console directory for TS generation
CONSOLE_DIR=$(realpath "../../../console")

# Compile C#
protoc --proto_path=$PROTO_DIR --csharp_out="$PROTO_DIR/../" "$PROTO_DIR"/RC2Proto.proto

# Compile TS/Node
protoc --plugin=protoc-gen-ts_proto=$CONSOLE_DIR/node_modules/.bin/protoc-gen-ts_proto.cmd \
    --ts_proto_out=$CONSOLE_DIR/generated \
    --ts_proto_opt=esModuleInterop=true \
    --ts_proto_opt=forceLong=bigint \
    -I "$PROTO_DIR" RC2Proto.proto