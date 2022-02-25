Hearty
========

_Hearty_ is currently in early drafting.  The author aspires to create
a server for _remote promises_, written in .NET 6.

_Promises_ represent the state of tasks that may be completed later, asynchronously.  
In .NET, ``Task`` and ``ValueTask`` are promise objects, where the asynchronous
state is tracked locally, within one process.  _Remote promises_ extend this idea 
to track the state on a remote server, so that multiple clients can share the same
result that the promise fulfills.

Purpose and Design Rationale
============================

Hearty is designed for clients to post _jobs_ that:

  * have complex input parameters which may vary a lot,
  * and are resource-intensive and time-consuming to
    provide results for.

Executing such jobs, then entails farming out work to multiple computers.  
And the jobs cannot be pre-computed to any significant degree; what jobs 
to run, within the limited resources and time available,
must be driven on-demand by requesting clients.

Owing to the expensive computation, there needs to be a dashboard to monitor 
and manage currently running jobs.  Jobs can be prioritized or killed.

The system could be summarized as a "job board", that also stores
them, until they expire, so that other clients with exactly the same jobs
to execute can re-use the old results.  The clients are the many users
within an organization, that during the day, need various analytics to be
computed on the shared data of the orgnization, and this system is a way of coordinating them.

The number of clients are expected to be at most in the thousands,
not millions.  Hearty is implemented to scale for that load, without
going to the extremes.  Only relatively few promises are expected to be 
actively updating, receiving results and forwarding them to clients, 
while many other ones can lay dormant. 

Although Hearty will support streaming of results, that is only to allow clients
to progressively consume them.  So there is a definite beginning and end to the 
data that clients are expected to access, like a movie, and unlike a TV channel.
Hearty is not specifically made to do low-latency "real-time streaming", 
as some other platforms are.

Hearty, at its core, is implemented as a .NET library, so that the server can
be customized to examine the jobs that clients post and intelligently schedule
their execution.

The application that Hearty is being made for currently uses _RabbitMQ_ to queue
requests and _Redis_ to store results.  The whole contraception has limited
functionality, and has not been reliable because there are too many moving parts.
What Hearty is essentially doing is combining, into one microservice, 
a subset of functionality from RabbitMQ and Redis that the application needs, 
and adding on top the desired application-specific scheduling logic and 
business logic. 

A microservice architecture is sometimes criticized for having too many moving
parts, but the application described above does justify a microservice, because 
no matter what there needs to be some central point where jobs are collected
and farmed out.

Through custom logic, Hearty also allows the promises it stores to be replicated
elsewhere.  If the connections from the clients can be arranged to be switched
over to the replica, then the system can be resilient against hardware or network
failures.

Hearty communicates with its clients primarily through HTTP and WebSockets.
The on-wire messaging format is not the most efficient possible, but prizes
ease of debugging, tracing and implementation using common libraries in popular
programming languages.  HTTP and WebSockets can be easily proxied, such as by
an ingress in a Kubernetes cluster, can be consumed in standard Web 
clients/browsers, and can integrate with modern security mechanisms. No 
non-standard ports need to be opened in firewalls.
