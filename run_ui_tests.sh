# Build the project first
dotnet build SmartFactory.MES.csproj

# Start the application in background without building
dotnet run --project SmartFactory.MES.csproj --urls "http://localhost:5000" --no-build > app.log 2>&1 &
APP_PID=$!

echo "Application started with PID $APP_PID. Waiting for it to be ready..."

# Wait for the app to be ready
timeout=60
counter=0
while ! curl -s http://localhost:5000 > /dev/null; do
    sleep 1
    counter=$((counter+1))
    if [ $counter -ge $timeout ]; then
        echo "Timeout reached. Application did not start."
        kill $APP_PID
        exit 1
    fi
done

echo "Application is ready. Running UI tests..."

# Run the tests
# Build the tests
dotnet build SmartFactory.MES.UITests/SmartFactory.MES.UITests.csproj

# Install browsers
~/.dotnet/tools/playwright install

# Run tests with debug logging
DEBUG=pw:browser* dotnet test SmartFactory.MES.UITests/SmartFactory.MES.UITests.csproj > test_output.log 2>&1

TEST_EXIT_CODE=$?

# Kill the application
kill $APP_PID

echo "Tests finished with exit code $TEST_EXIT_CODE"
exit $TEST_EXIT_CODE
