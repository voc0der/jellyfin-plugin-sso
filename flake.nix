{
  inputs = { nixpkgs.url = "github:nixos/nixpkgs/nixpkgs-unstable"; };

  outputs = { self, nixpkgs }:
    let pkgs = nixpkgs.legacyPackages.x86_64-linux;
    in {
      devShell.x86_64-linux =
        pkgs.mkShell { buildInputs = [ pkgs.nodejs_24 pkgs.dotnet-sdk_10 pkgs.python3 ]; };
   };
}
