﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using CNTK;

namespace CntkExtensions.Models
{
    public class Sequential
    {
        Learner m_learner;
        Function m_loss;
        Function m_metric;

        Variable m_inputVariable;
        Variable m_targetVariable;

        public Function Network;

        public Sequential(Variable input)
        {
            Network = input;
        }

        public void Add(Func<Function, Function> layerCreator)
        {
            Network = layerCreator(Network);
        }

        public void Compile(Func<IList<Parameter>, Learner> learnerCreator,
            Func<Variable, Variable, Function> lossCreator,
            Func<Variable, Variable, Function> metricCreator)
        {
            // configuration
            var d = Layers.GlobalDevice;
            var dataType = Layers.GlobalDataType;

            // Get input and target variables from network.
            m_inputVariable = Network.Arguments[0];
            var targetShape = Network.Output.Shape;
            m_targetVariable = Variable.InputVariable(targetShape, dataType);

            // Setup loss and metric.
            m_loss = lossCreator(m_targetVariable, Network.Output);
            m_metric = metricCreator(m_targetVariable, Network.Output);

            // create learner and trainer.
            m_learner = learnerCreator(Network.Parameters());
        }

        public void Fit(Tensor x = null, Tensor y = null, int batchSize = 32, int epochs = 1)
        {
            // configuration
            var d = Layers.GlobalDevice;

            // setup minibatch source.
            var minibatchSource = new MemoryMinibatchSource(x, y, seed: 5, randomize: true);
            
            // setup trainer.
            var trainer = CNTKLib.CreateTrainer(Network, m_loss, m_metric, new LearnerVector { m_learner });

            // variables for training loop.            
            var inputMap = new Dictionary<Variable, Value>();

            var lossSum = 0.0;
            var metricSum = 0.0;
            var totalSampleCount = 0;

            for (int epoch = 0; epoch < epochs; )
            {
                var minibatchData = minibatchSource.GetNextMinibatch(batchSize);

                var isSweepEnd = minibatchData.isSweepEnd;
                var observations = minibatchData.observations;
                var targets = minibatchData.targets;

                // Note that it is possible to create a batch using a data buffer array, to reduce allocations. 
                // However, unsure how to handle random shuffling in this case.
                using (Value batchObservations = Value.CreateBatch<float>(m_inputVariable.Shape, observations, d))
                using (Value batchTarget = Value.CreateBatch<float>(m_targetVariable.Shape, targets, d))
                {
                    inputMap.Add(m_inputVariable, batchObservations);
                    inputMap.Add(m_targetVariable, batchTarget);

                    trainer.TrainMinibatch(inputMap, false, d);

                    var lossValue = (float)trainer.PreviousMinibatchLossAverage();
                    var metricValue = (float)trainer.PreviousMinibatchEvaluationAverage();

                    //Trace.WriteLine($"Loss: {lossValue}, Metric: {metricValue}");

                    // Accumulate loss/metric.
                    lossSum += lossValue * batchSize;
                    metricSum += metricValue * batchSize;
                    totalSampleCount += batchSize;

                    inputMap.Clear();

                    if(isSweepEnd)
                    {
                        var currentLoss = lossSum / totalSampleCount;
                        var currentMetric = metricSum / totalSampleCount;
                        Trace.WriteLine($"Epoch: {epoch + 1} Loss = {currentLoss:F16}, Metric = {currentMetric:F16}");

                        ++epoch;
                        lossSum = 0;
                        metricSum = 0;
                        totalSampleCount = 0;
                    }

                    // Ensure cleanup, call erase.
                    batchObservations.Erase();
                    batchTarget.Erase();
                }
            }
        }

        public (float loss, float metric) Evaluate(Tensor x = null, Tensor y = null, int batchSize = 32)
        {
            // configuration
            var d = Layers.GlobalDevice;

            // setup minibatch source.
            var minibatchSource = new MemoryMinibatchSource(x, y, seed: 5, randomize: false);

            // create learner and trainer.
            var lossEvaluator = CNTKLib.CreateEvaluator(m_loss);
            var metricEvaluator = CNTKLib.CreateEvaluator(m_metric);

            // variables for training loop.            
            var inputMap = new UnorderedMapVariableMinibatchData();//new Dictionary<Variable, Value>();

            var lossSum = 0.0;
            var metricSum = 0.0;
            var totalSampleCount = 0;

            bool isSweepEnd = false;

            while (!isSweepEnd)
            {
                var minibatchData = minibatchSource.GetNextMinibatch(batchSize);

                isSweepEnd = minibatchData.isSweepEnd;
                var observations = minibatchData.observations;
                var targets = minibatchData.targets;

                // Note that it is possible to create a batch using a data buffer array, to reduce allocations. 
                // However, unsure how to handle random shuffling in this case.
                using (var batchObservations = new MinibatchData(Value.CreateBatch<float>(m_inputVariable.Shape, observations, d)))
                using (var batchTarget = new MinibatchData(Value.CreateBatch<float>(m_targetVariable.Shape, targets, d)))
                {
                    inputMap.Add(m_inputVariable, batchObservations);
                    inputMap.Add(m_targetVariable, batchTarget);

                    var lossValue = lossEvaluator.TestMinibatch(inputMap);
                    var metricValue = metricEvaluator.TestMinibatch(inputMap);

                    // Accumulate loss/metric.
                    lossSum += lossValue * batchSize;
                    metricSum += metricValue * batchSize;
                    totalSampleCount += batchSize;

                    inputMap.Clear();
                }
            }

            var finalLoss = lossSum / totalSampleCount;
            var finalMetric = metricSum / totalSampleCount;

            return ((float)finalLoss, (float)finalMetric);
        }
    }
}