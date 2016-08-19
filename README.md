# BackFlip

Amazon Cloud Mirror Service

BackFlip plans to be a service that allows you to mirror one or more directories to Amazon Cloud Drive, from one or more machines.

Files are uploaded to the specified directory - and conflicts are resolved automatically by choosing the most recently created/modified file, and archiving the older versions into the .backflip directory.

Initial build is a one directional sync, but two-directional sync is intended for the future

.flipignore files can be used, similar to .gitignore files, to exclude files from the sync

If files have manually been uploaded to Amazon Cloud Drive, BackFlip automatically compares the MD5 hash of the file before attempting to upload, and will not upload files that are already in an identical state on the server.

BackFlip does NOT encrypt any files, to ensure files preserve all functionality with Amazon apps and online-tools, as well as keeping photos eligible for Photo storage quotas, etc.

BackFlip DOES generate a .backflip directory inside each folder in your Cloud Drive. When 2-way sync is completed, these folders will NOT be copied down to your local machines - these are only required for optimizing the comparison algorithms, and may be removed entirely in the near future.
