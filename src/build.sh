pushd `dirname $0` > /dev/null
SCRIPTPATH=`pwd`
popd > /dev/null

export CONFIGURATION=Release
export OutDir=$SCRIPTPATH/../Binaries/$CONFIGURATION
xbuild /p:Configuration=$CONFIGURATION $SCRIPTPATH/Orleans.sln
xbuild /p:Configuration=$CONFIGURATION $SCRIPTPATH/Orleans.sln  # need to build twice, no idea why it works the second time (maybe some race condition?)

