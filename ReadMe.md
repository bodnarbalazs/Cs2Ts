# C# to Ts generation:
C:\Users\User\Desktop\LC\src\backend\LC.Domain> cs2ts "..\..\frontend\src\generated"

Run `cs2ts  "..\..\frontend\src\generated"`
from the Domain folder

How to install:

`dotnet tool install --global --add-source ./nupkg Cs2Ts`

from the downloaded solution from github. (https://github.com/bodnarbalazs/Cs2Ts) If it's turned into a global package it's easier.
if it's changed `dotnet pack`, then `dotnet tool uninstall --global Cs2Ts ` for good measure then reinstall it.
