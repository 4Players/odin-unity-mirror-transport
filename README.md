# 4Players ODIN Transport for Mirror Networking

This is a transport for the Mirror Networking Multiplayer Framework for Unity which uses [ODIN](https://www.4players.io/odin/) 
to send and receive data.

This `master` version is using the `Odin Handler` component. As `OdinTransport` requires a special peer 
user data to identify which user is server/host and which is client, it's not possible to use the 
`Odin Handler` component in a multiroom scenario. It works fine for a single room though. However, if 
multiple rooms are used it gets very unstable and buggy.

The recommended way is to leave out `Odin Handler` and just to use `Odin Client`. This implementation can
be found in the `odin_client` branch and should be the way to move forward (i.e. will soon be merged into
`master`).
