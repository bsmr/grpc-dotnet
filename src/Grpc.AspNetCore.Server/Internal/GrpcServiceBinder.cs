﻿#region Copyright notice and license

// Copyright 2019 The gRPC Authors
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

#endregion

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;

namespace Grpc.AspNetCore.Server.Internal
{
    /// <summary>
    /// Service binder that is passed to ServiceImpl.BindService(ServiceBinderBase, ServiceImpl).
    /// This will execute the call handler factory and create call handlers.
    /// </summary>
    internal class GrpcServiceBinder<TService> : ServiceBinderBase where TService : class
    {
        private readonly IEndpointRouteBuilder _builder;
        private readonly ServiceMethodsRegistry _serviceMethodsRegistry;
        private readonly ServerCallHandlerFactory<TService> _serverCallHandlerFactory;
        private readonly IGrpcMethodModelFactory<TService> _serviceModelFactory;
        private readonly string _basePath;

        internal IList<IEndpointConventionBuilder> EndpointConventionBuilders { get; } = new List<IEndpointConventionBuilder>();
        internal IList<IMethod> ServiceMethods { get; } = new List<IMethod>();

        internal GrpcServiceBinder(
            IEndpointRouteBuilder builder,
            IGrpcMethodModelFactory<TService> serviceModelFactory,
            string basePath,
            ServerCallHandlerFactory<TService> serverCallHandlerFactory,
            ServiceMethodsRegistry serviceMethodsRegistry)
        {
            _builder = builder;
            _serviceMethodsRegistry = serviceMethodsRegistry;
            _serverCallHandlerFactory = serverCallHandlerFactory;
            _serviceModelFactory = serviceModelFactory;
            _basePath = basePath;
        }

        public override void AddMethod<TRequest, TResponse>(Method<TRequest, TResponse> method, ClientStreamingServerMethod<TRequest, TResponse> handler)
        {
            var model = _serviceModelFactory.CreateClientStreamingModel(method);
            var callHandler = _serverCallHandlerFactory.CreateClientStreaming(method, model.Invoker);
            AddMethodCore(method, callHandler.HandleCallAsync, model.Metadata);
        }

        public override void AddMethod<TRequest, TResponse>(Method<TRequest, TResponse> method, DuplexStreamingServerMethod<TRequest, TResponse> handler)
        {
            var model = _serviceModelFactory.CreateDuplexStreamingModel(method);
            var callHandler = _serverCallHandlerFactory.CreateDuplexStreaming(method, model.Invoker);
            AddMethodCore(method, callHandler.HandleCallAsync, model.Metadata);
        }

        public override void AddMethod<TRequest, TResponse>(Method<TRequest, TResponse> method, ServerStreamingServerMethod<TRequest, TResponse> handler)
        {
            var model = _serviceModelFactory.CreateServerStreamingModel(method);
            var callHandler = _serverCallHandlerFactory.CreateServerStreaming(method, model.Invoker);
            AddMethodCore(method, callHandler.HandleCallAsync, model.Metadata);
        }

        public override void AddMethod<TRequest, TResponse>(Method<TRequest, TResponse> method, UnaryServerMethod<TRequest, TResponse> handler)
        {
            var model = _serviceModelFactory.CreateUnaryModel(method);
            var callHandler = _serverCallHandlerFactory.CreateUnary(method, model.Invoker);
            AddMethodCore(method, callHandler.HandleCallAsync, model.Metadata);
        }

        private void AddMethodCore(IMethod method, RequestDelegate requestDelegate, List<object> metadata)
        {
            ServiceMethods.Add(method);

            var pattern = string.IsNullOrEmpty(_basePath)
                ? method.FullName
                : Combine(_basePath, method.FullName);

            var resolvedMetadata = new List<object>();

            // IMethod is added as metadata for the endpoint
            resolvedMetadata.Add(method);
            resolvedMetadata.AddRange(metadata);

            EndpointConventionBuilders.Add(_builder.MapPost(pattern, $"gRPC - {pattern}", requestDelegate, resolvedMetadata.ToArray()));
        }

        private static string Combine(string uri1, string uri2)
        {
            uri1 = uri1.TrimEnd('/');
            uri2 = uri2.TrimStart('/');
            return uri1 + "/" + uri2;
        }

        internal void CreateUnimplementedEndpoints()
        {
            // Return UNIMPLEMENTED status for missing service:
            // - /{service}/{method} + content-type header = grpc/application
            if (_serviceMethodsRegistry.Methods.Count == 0)
            {
                // Only one unimplemented service endpoint is needed for the application
                CreateUnimplementedEndpoint("{unimplementedService}/{unimplementedMethod}", "gRPC - Unimplemented service", _serverCallHandlerFactory.CreateUnimplementedService());
            }

            // Return UNIMPLEMENTED status for missing method:
            // - /Package.Service/{method} + content-type header = grpc/application
            var serviceNames = ServiceMethods.Select(m => m.ServiceName).Distinct().ToList();

            // Typically there should be one service name for a type
            // In case the bind method sets up multiple services in one call we'll loop over them
            foreach (var serviceName in serviceNames)
            {
                if (_serviceMethodsRegistry.Methods.Any(m => string.Equals(m.ServiceName, serviceName, StringComparison.Ordinal)))
                {
                    // Only one unimplemented method endpoint is need for the service
                    continue;
                }

                CreateUnimplementedEndpoint(serviceName + "/{unimplementedMethod}", $"gRPC - Unimplemented method for {serviceName}", _serverCallHandlerFactory.CreateUnimplementedMethod());
            }

            _serviceMethodsRegistry.Methods.AddRange(ServiceMethods);
        }

        private void CreateUnimplementedEndpoint(string pattern, string displayName, RequestDelegate requestDelegate)
        {
            var routePattern = RoutePatternFactory.Parse(pattern, defaults: null, new { contentType = GrpcContentTypeConstraint.Instance });
            _builder.Map(routePattern, displayName, requestDelegate, new HttpMethodMetadata(new[] { "POST" }));
        }

        private class GrpcContentTypeConstraint : IRouteConstraint
        {
            public static readonly GrpcContentTypeConstraint Instance = new GrpcContentTypeConstraint();

            public bool Match(HttpContext httpContext, IRouter route, string routeKey, RouteValueDictionary values, RouteDirection routeDirection)
            {
                if (httpContext == null)
                {
                    return false;
                }

                return GrpcProtocolHelpers.IsGrpcContentType(httpContext.Request.ContentType);
            }

            private GrpcContentTypeConstraint()
            {
            }
        }
    }
}
