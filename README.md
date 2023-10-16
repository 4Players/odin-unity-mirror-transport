# 4Players ODIN Transport for Mirror Networking

This is a transport for the Mirror Networking Multiplayer Framework for Unity which uses [4Players ODIN](https://www.4players.io/odin/) 
to send and receive data.

ODIN Transport is designed to work in combination with the [ODIN Voice Chat service](https://www.4players.io/odin/),
but can be used without utilizing voice.

## Dependencies

- Mirror ([Documentation](https://mirror-networking.gitbook.io/docs/))
- ODIN Unity SDK ([Documentation](https://www.4players.io/odin/sdk/unity/))

## Setup

1. Install Mirror into your project using the package manager. Mirror is available on the [Unity Asset Store](https://assetstore.unity.com/packages/tools/network/mirror-129321).
2. Download the latest ODIN Unity SDK .unitypackage from the [Github Releases page](https://github.com/4Players/odin-sdk-unity/releases) and import into your project. 
3. Locate the `OdinManager_NetworkingVariant` prefab at `Packages/4Players ODIN Transport for Mirror Networking/Prefabs` and drop it into your scene.
4. Select the OdinManager GameObject, click on the `Manage Access` button under `Client Authentication` and generate an Access Key.
5. Attach a `NetworkManager` component to a GameObject in your Scene.
6. Attach a `Odin Transport` component to the same GameObject as the `NetworkManager`
7. Drag the `Odin Transport` component into the `Transport` slot on the `NetworkManager`

That's all you need to setup the Odin Transport layer in your project.

## More Information

For more information on Access Keys or how to setup ODIN Voice Chat in your project, take a look at our [ODIN-Unity Guides page](https://www.4players.io/odin/guides/unity/) or our [ODIN Basics Tutorial on Youtube](https://youtu.be/S3DFxkWut9c?list=PLAe4Im8mFTAsS12OyFfAVnSLoJ7kEFJ8V).
