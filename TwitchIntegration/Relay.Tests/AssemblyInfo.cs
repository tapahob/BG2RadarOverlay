// The relay reads the Twitch base URLs and its data directory out of environment variables at
// startup, and a harness sets those immediately before starting one - so two harnesses coming up
// at the same time would race over both. Serial execution costs a few seconds and removes the
// whole class of flake.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
